# Script to download and install Jellyfin-MPEG
$ErrorActionPreference = "Stop"

$url = "https://repo.jellyfin.org/files/ffmpeg/windows/latest-7.x/win64/jellyfin-ffmpeg_7.1.3-1_portable_win64-clang-gpl.zip"
$destDir = "C:\Users\Admin\Documents\coding2\SoftMedia\src\SoftMedia.Server\ffmpeg-bin"
$zipPath = "$destDir\jellyfin-ffmpeg.zip"

Write-Host "Recreating directory: $destDir"
if (Test-Path $destDir) { Remove-Item $destDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

Write-Host "Downloading Jellyfin-FFmpeg from $url..."
Invoke-WebRequest -Uri $url -OutFile $zipPath

Write-Host "Extracting..."
Expand-Archive -Path $zipPath -DestinationPath $destDir -Force

Write-Host "Locating binaries..."
$binPath = Get-ChildItem -Path $destDir -Recurse -Filter "ffmpeg.exe" | Select-Object -ExpandProperty DirectoryName

Write-Host "Found binaries at: $binPath"
# Move binaries to root of destDir for easier access
Copy-Item "$binPath\*" "$destDir" -Recurse -Force

Write-Host "Cleaning up..."
Remove-Item $zipPath -Force
# Remove the extracted subfolder
try {
    $subFolder = Get-ChildItem -Path $destDir -Directory | Where-Object { $_.Name -like "jellyfin-ffmpeg-*" }
    if ($subFolder) { Remove-Item $subFolder.FullName -Recurse -Force }
} catch {
    Write-Warning "Cleanup minor issue."
}

Write-Host "FFmpeg setup complete. Path: $destDir\ffmpeg.exe"
