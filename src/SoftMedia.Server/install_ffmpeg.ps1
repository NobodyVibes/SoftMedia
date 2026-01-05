# Robust Jellyfin-FFmpeg Installer
$ErrorActionPreference = "Stop"

$targetDir = "C:\Users\Admin\Documents\coding2\SoftMedia\src\SoftMedia.Server\ffmpeg-bin"
Write-Host "Cleaning target directory: $targetDir"
if (Test-Path $targetDir) { 
    Remove-Item $targetDir -Recurse -Force -ErrorAction SilentlyContinue 
}
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

$url = "https://repo.jellyfin.org/files/ffmpeg/windows/latest-7.x/win64/jellyfin-ffmpeg_7.1.3-1_portable_win64-clang-gpl.zip"
$zip = "$targetDir\jellyfin.zip"

Write-Host "Downloading from $url..."
Invoke-WebRequest -Uri $url -OutFile $zip

Write-Host "Extracting..."
Expand-Archive -Path $zip -DestinationPath $targetDir -Force

Write-Host "Locating and moving binaries..."
$bin = Get-ChildItem -Path $targetDir -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
if ($null -eq $bin) { throw "ffmpeg.exe not found in archive!" }
Move-Item $bin.FullName "$targetDir\ffmpeg.exe" -Force

$probe = Get-ChildItem -Path $targetDir -Recurse -Filter "ffprobe.exe" | Select-Object -First 1
if ($probe) { Move-Item $probe.FullName "$targetDir\ffprobe.exe" -Force }

Write-Host "Cleaning up..."
Remove-Item $zip -Force
# Clean up subfolders
Get-ChildItem -Path $targetDir -Directory | Remove-Item -Recurse -Force

Write-Host "Success: Installed to $targetDir\ffmpeg.exe"
