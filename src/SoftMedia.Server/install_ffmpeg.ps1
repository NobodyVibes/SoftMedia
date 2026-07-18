<#
.SYNOPSIS
    Fetch the official jellyfin-ffmpeg portable build (Windows) into ./ffmpeg-bin.

.DESCRIPTION
    SoftMedia REQUIRES jellyfin-ffmpeg: it is the build that ships the `chromaprint` muxer used by
    intro/credits detection (`ffmpeg -f chromaprint`), plus the NVENC/QSV/AMF/VAAPI hardware paths.
    Generic Gyan.FFmpeg / distro ffmpeg builds lack chromaprint and are NOT acceptable.

    The binary is fetched from Jellyfin's official server at install time and is NOT committed to git
    (see docs/plans/licensing-and-repo-hygiene-plan-2026-06-18.md). jellyfin-ffmpeg is GPL-3.0-or-later;
    obtaining it directly from upstream keeps SoftMedia clear of redistribution obligations.

.PARAMETER Version
    Pinned jellyfin-ffmpeg version (default 7.1.4-3). Stay on the 7.x line - 8.x is pre-release.

.PARAMETER TrackLatest
    Ignore -Version and resolve the current filename from the channel pointer file
    (win64-clang-gpl.txt). Use this if the pinned version has been pruned from the rolling channel.

.PARAMETER ExpectedSha256
    Optional. If supplied, the downloaded zip is verified against this hash (upstream publishes no
    hash next to the zip, so capture it once per pinned version). If omitted, integrity rests on TLS.
#>
[CmdletBinding()]
param(
    [string]$Version = "7.1.4-3",
    [switch]$TrackLatest,
    [string]$ExpectedSha256 = ""
)
$ErrorActionPreference = "Stop"

$TargetDir = Join-Path $PSScriptRoot "ffmpeg-bin"
$Base = "https://repo.jellyfin.org/files/ffmpeg/windows/latest-7.x/win64"
$ffmpegExe = Join-Path $TargetDir "ffmpeg.exe"

function Test-Chromaprint {
    param([string]$Exe)
    if (-not (Test-Path $Exe)) { return $false }
    $ver = & $Exe -hide_banner -version 2>$null
    $mux = & $Exe -hide_banner -muxers 2>$null
    return (($ver -match "--enable-chromaprint") -and ($mux -match "chromaprint"))
}

# Idempotent: skip if a chromaprint-enabled ffmpeg is already present.
if (Test-Chromaprint $ffmpegExe) {
    Write-Host "[OK] jellyfin-ffmpeg already present with chromaprint at $ffmpegExe" -ForegroundColor Green
    return
}

if ($TrackLatest) {
    $pointer = "$Base/win64-clang-gpl.txt"
    Write-Host "[>] Resolving current build from channel pointer: $pointer" -ForegroundColor Cyan
    $zipName = (Invoke-WebRequest -Uri $pointer -UseBasicParsing).Content.Trim()
}
else {
    $zipName = "jellyfin-ffmpeg_${Version}_portable_win64-clang-gpl.zip"
}
$url = "$Base/$zipName"

Write-Host "[>] Preparing $TargetDir" -ForegroundColor Cyan
if (Test-Path $TargetDir) { Remove-Item $TargetDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

$zip = Join-Path $TargetDir "jellyfin-ffmpeg.zip"
Write-Host "[>] Downloading $url" -ForegroundColor Cyan
Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

if ($ExpectedSha256) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash
    if ($actual -ne $ExpectedSha256.ToUpper()) {
        throw "SHA-256 mismatch. Expected $ExpectedSha256, got $actual. Aborting."
    }
    Write-Host "[OK] SHA-256 verified" -ForegroundColor Green
}
else {
    Write-Warning "No -ExpectedSha256 supplied; integrity rests on TLS only. Capture and pin the hash for releases."
}

Write-Host "[>] Extracting..." -ForegroundColor Cyan
Expand-Archive -Path $zip -DestinationPath $TargetDir -Force

# The portable zip nests the binaries; relocate ffmpeg.exe + ffprobe.exe to $TargetDir root.
foreach ($name in @("ffmpeg.exe", "ffprobe.exe")) {
    $found = Get-ChildItem -Path $TargetDir -Recurse -Filter $name | Select-Object -First 1
    if ($null -eq $found) { throw "$name not found in the downloaded archive." }
    if ($found.FullName -ne (Join-Path $TargetDir $name)) {
        Move-Item $found.FullName (Join-Path $TargetDir $name) -Force
    }
}
Remove-Item $zip -Force -ErrorAction SilentlyContinue
# Remove the nested extraction subfolders, leaving ffmpeg.exe/ffprobe.exe at the root.
Get-ChildItem -Path $TargetDir -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

if (-not (Test-Chromaprint $ffmpegExe)) {
    throw "Downloaded ffmpeg lacks the chromaprint muxer - wrong build. SoftMedia requires jellyfin-ffmpeg."
}
Write-Host "[OK] jellyfin-ffmpeg installed at $TargetDir (chromaprint verified)" -ForegroundColor Green
