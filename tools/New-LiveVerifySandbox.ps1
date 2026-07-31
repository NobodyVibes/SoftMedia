<#
.SYNOPSIS
  SM-WI-003 - builds the LiveVerify sandbox by COPYING a slice of the real libraries.

.DESCRIPTION
  Live verification must never scan the production library folders (scans purge rows for
  files missing on disk). This script builds a disposable sandbox tree the operator points
  scratch libraries at:
    <Destination>\Movies  - every movie, loose root files wrapped into per-title folders
    <Destination>\TV      - up to -MaxSeries series folders, -EpisodesPerSeason episodes
                            per season (season parsed from SxxEyy / NNxNN in the name;
                            season 0 = specials always included)
    <Destination>\Music   - up to -MaxArtists artist trees, -MaxAlbumsPerArtist albums each
    <Destination>\Books   - up to -MaxBooks book files, relative structure preserved

  Copy-only, re-runnable (existing same-size files are skipped), never touches sources.
  Defaults for the source roots were read from the live DB on 2026-07-28 via
  tools/FixtureHarvester (which prints them on every run).

.EXAMPLE
  powershell -File tools\New-LiveVerifySandbox.ps1 -EpisodesPerSeason 2
#>
[CmdletBinding()]
param(
    [string]$Destination = "$env:USERPROFILE\SoftMedia-LiveVerify",
    [string]$MoviesRoot = "C:\Users\Admin\Videos\Movies",
    [string]$TvRoot = "C:\Users\Admin\Videos\tv",
    [string]$MusicRoot = "C:\Users\Admin\Music",
    [string]$BooksRoot = "C:\Users\Admin\Videos\book",
    [int]$MaxSeries = 2,
    [int]$EpisodesPerSeason = 2,
    [int]$MaxSpecials = 4,
    [int]$MaxArtists = 2,
    [int]$MaxAlbumsPerArtist = 3,
    [int]$MaxBooks = 10,
    # Videos above this size are skipped (the 11 GB BDRips add copy time, not coverage;
    # scene-named small.soldiers at ~7.6 GB stays in as the big-file fixture).
    [double]$MaxMovieFileGB = 8
)

$ErrorActionPreference = 'Stop'

$videoExt = @('.mkv', '.mp4', '.avi', '.webm', '.m4v', '.mov', '.ts')
$audioExt = @('.mp3', '.flac', '.m4a', '.ogg', '.wav', '.opus')
$bookExt = @('.epub', '.pdf', '.cbz', '.cbr', '.mobi', '.azw3')

$script:copied = 0
$script:skipped = 0
$script:bytes = 0L

function Copy-Slice {
    param([string]$Source, [string]$Target)
    if (Test-Path $Target) {
        $srcLen = (Get-Item $Source).Length
        $dstLen = (Get-Item $Target).Length
        if ($srcLen -eq $dstLen) { $script:skipped++; return }
    }
    $dir = Split-Path $Target -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    Copy-Item -LiteralPath $Source -Destination $Target
    $script:copied++
    $script:bytes += (Get-Item $Target).Length
}

function Get-SeasonNumber {
    param([string]$Name)
    if ($Name -match '(?i)S(\d{1,2})E\d{1,3}') { return [int]$Matches[1] }
    if ($Name -match '(?i)(?<![0-9])(\d{1,2})x\d{1,3}') { return [int]$Matches[1] }
    return $null
}

Write-Host "LiveVerify sandbox -> $Destination"
New-Item -ItemType Directory -Force $Destination | Out-Null

# ── Movies: everything under the size cap; loose videos get per-title folders;
#    sidecars (nfo/jpg/srt/...) always come along — they are live NFO/artwork fixtures ──
if (Test-Path $MoviesRoot) {
    Write-Host "Movies from $MoviesRoot"
    $capBytes = [long]($MaxMovieFileGB * 1GB)
    Get-ChildItem -LiteralPath $MoviesRoot -Recurse -File | ForEach-Object {
        $isVideo = $videoExt -contains $_.Extension.ToLower()
        if ($isVideo -and $_.Length -gt $capBytes) { return }
        $rel = $_.FullName.Substring($MoviesRoot.Length).TrimStart('\')
        if ($isVideo -and -not $rel.Contains('\')) {
            # Loose root video: wrap into a per-title folder (LiveVerify convention).
            $stem = [IO.Path]::GetFileNameWithoutExtension($_.Name)
            $rel = "$stem\$($_.Name)"
        }
        Copy-Slice $_.FullName (Join-Path $Destination "Movies\$rel")
    }
}

# ── TV: prefer series that actually have episodes, specials-bearing ones first
#    (the plan requires >=1 series with specials); EpisodesPerSeason per season ──
if (Test-Path $TvRoot) {
    Write-Host "TV from $TvRoot"
    $seriesInfo = Get-ChildItem -LiteralPath $TvRoot -Directory | ForEach-Object {
        $eps = @(Get-ChildItem -LiteralPath $_.FullName -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $videoExt -contains $_.Extension.ToLower() })
        $hasSpecials = ($eps | Where-Object { (Get-SeasonNumber $_.Name) -eq 0 }).Count -gt 0
        [pscustomobject]@{ Dir = $_; Episodes = $eps; HasSpecials = $hasSpecials }
    } | Where-Object { $_.Episodes.Count -gt 0 } |
        Sort-Object @{e = { $_.HasSpecials }; Descending = $true }, @{e = { $_.Episodes.Count }; Descending = $true }

    $seriesInfo | Select-Object -First $MaxSeries | ForEach-Object {
        $bySeason = $_.Episodes | Group-Object { Get-SeasonNumber $_.Name }
        foreach ($group in $bySeason) {
            if ($group.Name -eq '0') {
                $take = $group.Group | Sort-Object Name | Select-Object -First $MaxSpecials
            }
            else {
                $take = $group.Group | Sort-Object Name | Select-Object -First $EpisodesPerSeason
            }
            foreach ($ep in $take) {
                $rel = $ep.FullName.Substring($TvRoot.Length).TrimStart('\')
                Copy-Slice $ep.FullName (Join-Path $Destination "TV\$rel")
            }
        }
    }
}

# ── Music: artist trees with the most album folders first ──
if (Test-Path $MusicRoot) {
    Write-Host "Music from $MusicRoot"
    $artists = Get-ChildItem -LiteralPath $MusicRoot -Directory | ForEach-Object {
        $albumDirs = Get-ChildItem -LiteralPath $_.FullName -Recurse -Directory | Where-Object {
            $dir = $_
            (Get-ChildItem -LiteralPath $dir.FullName -File | Where-Object { $audioExt -contains $_.Extension.ToLower() }).Count -gt 0
        }
        [pscustomobject]@{ Dir = $_; AlbumDirs = @($albumDirs) }
    } | Where-Object { $_.AlbumDirs.Count -ge 1 } | Sort-Object { $_.AlbumDirs.Count } -Descending
    $artists | Select-Object -First $MaxArtists | ForEach-Object {
        foreach ($album in ($_.AlbumDirs | Sort-Object FullName | Select-Object -First $MaxAlbumsPerArtist)) {
            Get-ChildItem -LiteralPath $album.FullName -File | ForEach-Object {
                $rel = $_.FullName.Substring($MusicRoot.Length).TrimStart('\')
                Copy-Slice $_.FullName (Join-Path $Destination "Music\$rel")
            }
        }
    }
}

# ── Books ──
if (Test-Path $BooksRoot) {
    Write-Host "Books from $BooksRoot"
    Get-ChildItem -LiteralPath $BooksRoot -Recurse -File |
        Where-Object { $bookExt -contains $_.Extension.ToLower() } |
        Sort-Object FullName | Select-Object -First $MaxBooks | ForEach-Object {
            $rel = $_.FullName.Substring($BooksRoot.Length).TrimStart('\')
            Copy-Slice $_.FullName (Join-Path $Destination "Books\$rel")
        }
}

@"
SoftMedia LiveVerify sandbox (SM-WI-003) - DISPOSABLE test data.
Built $(Get-Date -Format s) by tools\New-LiveVerifySandbox.ps1 from copies of the real
libraries. Point SCRATCH SoftMedia libraries here for live verification; never point a
test server at the production library folders (scans purge rows for missing files).
Safe to delete this whole folder at any time; re-run the script to rebuild.
"@ | Out-File -Encoding utf8 (Join-Path $Destination 'README.txt')

$gb = [math]::Round($script:bytes / 1GB, 2)
Write-Host "Done: $($script:copied) files copied ($gb GB), $($script:skipped) already present."
