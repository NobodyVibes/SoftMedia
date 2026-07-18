#Requires -Version 5.1
<#
.SYNOPSIS
    SoftMedia Setup Script - Installs all dependencies and configures the application.

.DESCRIPTION
    This script checks for and installs the following prerequisites:
    - .NET 8 SDK
    - Node.js (LTS)
    - FFmpeg
    
    It then restores NuGet packages for the server and npm packages for the client.

.PARAMETER SkipPrerequisites
    Skip checking/installing prerequisites (useful if already installed).

.PARAMETER StartServices
    Start both server and client after setup completes.

.EXAMPLE
    .\setup.ps1
    
.EXAMPLE
    .\setup.ps1 -StartServices
    
.EXAMPLE
    .\setup.ps1 -SkipPrerequisites

.NOTES
    Requires Windows 10 or later.
    Run as Administrator for best results (required for installing prerequisites).
#>

[CmdletBinding()]
param(
    [switch]$SkipPrerequisites,
    [switch]$StartServices
)

# ============================================================================
# Configuration
# ============================================================================
$ErrorActionPreference = "Stop"
$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$ServerPath = Join-Path $ScriptRoot "src\SoftMedia.Server"
$ClientPath = Join-Path $ScriptRoot "src\SoftMedia.Client"

# Colors for output
function Write-Header { param($Message) Write-Host "`n========================================" -ForegroundColor Cyan; Write-Host " $Message" -ForegroundColor Cyan; Write-Host "========================================" -ForegroundColor Cyan }
function Write-Success { param($Message) Write-Host "[OK] $Message" -ForegroundColor Green }
function Write-Warning { param($Message) Write-Host "[!] $Message" -ForegroundColor Yellow }
function Write-Error { param($Message) Write-Host "[X] $Message" -ForegroundColor Red }
function Write-Info { param($Message) Write-Host "[>] $Message" -ForegroundColor White }

# ============================================================================
# Helper Functions
# ============================================================================
function Test-Administrator {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentUser)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-CommandExists {
    param($Command)
    $null -ne (Get-Command $Command -ErrorAction SilentlyContinue)
}

function Get-WingetAvailable {
    return Test-CommandExists "winget"
}

function Install-WithWinget {
    param($PackageId, $PackageName)
    
    if (-not (Get-WingetAvailable)) {
        Write-Warning "Winget not available. Please install $PackageName manually."
        return $false
    }
    
    Write-Info "Installing $PackageName via winget..."
    try {
        winget install --id $PackageId --accept-package-agreements --accept-source-agreements --silent
        return $true
    }
    catch {
        Write-Error "Failed to install $PackageName : $_"
        return $false
    }
}

# ============================================================================
# Prerequisite Checks
# ============================================================================
function Test-DotNetSdk {
    if (-not (Test-CommandExists "dotnet")) {
        return $false
    }
    
    # Check for .NET 8
    $sdks = dotnet --list-sdks 2>$null
    return $sdks -match "^8\."
}

function Test-NodeJs {
    if (-not (Test-CommandExists "node")) {
        return $false
    }
    
    $version = node --version 2>$null
    # Require Node.js 18+ (LTS)
    if ($version -match "v(\d+)\.") {
        return [int]$Matches[1] -ge 18
    }
    return $false
}

function Get-SoftMediaFFmpeg {
    # Prefer the fetched jellyfin-ffmpeg in the server's ffmpeg-bin; else any ffmpeg on PATH.
    $local = Join-Path $ServerPath "ffmpeg-bin\ffmpeg.exe"
    if (Test-Path $local) { return $local }
    if (Test-CommandExists "ffmpeg") { return "ffmpeg" }
    return $null
}

function Test-FFmpeg {
    # SoftMedia REQUIRES the jellyfin-ffmpeg build: it carries the chromaprint muxer used by
    # intro/credits detection (`ffmpeg -f chromaprint`). A bare/Gyan/distro ffmpeg without it is
    # treated as "not installed" so setup fetches the correct build.
    $exe = Get-SoftMediaFFmpeg
    if (-not $exe) { return $false }
    $ver = & $exe -hide_banner -version 2>$null
    $mux = & $exe -hide_banner -muxers 2>$null
    return (($ver -match "--enable-chromaprint") -and ($mux -match "chromaprint"))
}

function Install-Prerequisites {
    Write-Header "Checking Prerequisites"
    
    $isAdmin = Test-Administrator
    if (-not $isAdmin) {
        Write-Warning "Not running as Administrator. Some installations may require manual steps."
    }
    
    # Check .NET 8 SDK
    Write-Info "Checking .NET 8 SDK..."
    if (Test-DotNetSdk) {
        Write-Success ".NET 8 SDK is installed"
    }
    else {
        Write-Warning ".NET 8 SDK not found"
        if ($isAdmin -and (Get-WingetAvailable)) {
            Install-WithWinget "Microsoft.DotNet.SDK.8" ".NET 8 SDK"
            # Refresh PATH
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        }
        else {
            Write-Info "Please install .NET 8 SDK from: https://dotnet.microsoft.com/download/dotnet/8.0"
            Write-Info "After installation, restart this script."
            if (-not $isAdmin) {
                Write-Info "Or run this script as Administrator to auto-install."
            }
        }
    }
    
    # Check Node.js
    Write-Info "Checking Node.js..."
    if (Test-NodeJs) {
        $version = node --version
        Write-Success "Node.js $version is installed"
    }
    else {
        Write-Warning "Node.js 18+ not found"
        if ($isAdmin -and (Get-WingetAvailable)) {
            Install-WithWinget "OpenJS.NodeJS.LTS" "Node.js LTS"
            # Refresh PATH
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path", "User")
        }
        else {
            Write-Info "Please install Node.js LTS from: https://nodejs.org/"
            Write-Info "After installation, restart this script."
            if (-not $isAdmin) {
                Write-Info "Or run this script as Administrator to auto-install."
            }
        }
    }
    
    # Check FFmpeg - SoftMedia REQUIRES jellyfin-ffmpeg (chromaprint muxer). Gyan/distro builds won't do.
    Write-Info "Checking jellyfin-ffmpeg (chromaprint-enabled)..."
    if (Test-FFmpeg) {
        Write-Success "jellyfin-ffmpeg (chromaprint) is available"
    }
    else {
        Write-Warning "jellyfin-ffmpeg not found (or the ffmpeg on PATH lacks the chromaprint muxer)"
        $ffmpegInstaller = Join-Path $ServerPath "install_ffmpeg.ps1"
        if (Test-Path $ffmpegInstaller) {
            Write-Info "Fetching the official jellyfin-ffmpeg build via install_ffmpeg.ps1..."
            try { & $ffmpegInstaller } catch { Write-Warning "jellyfin-ffmpeg fetch failed: $_" }
        }
        else {
            Write-Info "Download jellyfin-ffmpeg from https://repo.jellyfin.org/ and place ffmpeg.exe/ffprobe.exe"
            Write-Info "in src\SoftMedia.Server\ffmpeg-bin (or set FFmpeg:Path). Gyan/distro ffmpeg lacks chromaprint."
        }
    }
    
    # Final check
    Write-Host ""
    $allGood = $true
    
    if (-not (Test-DotNetSdk)) {
        Write-Error ".NET 8 SDK is required but not installed"
        $allGood = $false
    }
    
    if (-not (Test-NodeJs)) {
        Write-Error "Node.js 18+ is required but not installed"
        $allGood = $false
    }
    
    if (-not (Test-FFmpeg)) {
        Write-Warning "jellyfin-ffmpeg (chromaprint) not available. Transcoding and intro/credits detection will not work."
        # Don't hard-fail on ffmpeg - the server still starts; transcoding/fingerprinting degrade.
    }
    
    return $allGood
}

# ============================================================================
# Project Setup
# ============================================================================
function Install-ServerDependencies {
    Write-Header "Setting Up Server (.NET)"
    
    if (-not (Test-Path $ServerPath)) {
        Write-Error "Server path not found: $ServerPath"
        return $false
    }
    
    Push-Location $ServerPath
    try {
        Write-Info "Restoring NuGet packages..."
        dotnet restore
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to restore NuGet packages"
            return $false
        }
        Write-Success "Server dependencies restored"
        
        Write-Info "Building server (Debug)..."
        dotnet build --no-restore
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to build server"
            return $false
        }
        Write-Success "Server built successfully"
        
        return $true
    }
    finally {
        Pop-Location
    }
}

function Install-ClientDependencies {
    Write-Header "Setting Up Client (React/Vite)"
    
    if (-not (Test-Path $ClientPath)) {
        Write-Error "Client path not found: $ClientPath"
        return $false
    }
    
    Push-Location $ClientPath
    try {
        Write-Info "Installing npm packages..."
        npm install
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Failed to install npm packages"
            return $false
        }
        Write-Success "Client dependencies installed"
        
        return $true
    }
    finally {
        Pop-Location
    }
}

# ============================================================================
# Start Services
# ============================================================================
function Start-SoftMediaServices {
    Write-Header "Starting SoftMedia"
    
    Write-Info "Starting server on http://localhost:5000..."
    $serverJob = Start-Job -ScriptBlock {
        param($path)
        Set-Location $path
        dotnet run
    } -ArgumentList $ServerPath
    
    # Wait a moment for server to start
    Start-Sleep -Seconds 3
    
    Write-Info "Starting client on http://localhost:5173..."
    $clientJob = Start-Job -ScriptBlock {
        param($path)
        Set-Location $path
        npm run dev
    } -ArgumentList $ClientPath
    
    Write-Success "Services started!"
    Write-Host ""
    Write-Host "  Server: http://localhost:5000" -ForegroundColor Cyan
    Write-Host "  Client: http://localhost:5173" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Press Ctrl+C to stop services..." -ForegroundColor Yellow
    
    try {
        while ($true) {
            Start-Sleep -Seconds 1
        }
    }
    finally {
        Write-Info "Stopping services..."
        Stop-Job $serverJob -ErrorAction SilentlyContinue
        Stop-Job $clientJob -ErrorAction SilentlyContinue
        Remove-Job $serverJob -ErrorAction SilentlyContinue
        Remove-Job $clientJob -ErrorAction SilentlyContinue
    }
}

# ============================================================================
# Main
# ============================================================================
function Main {
    Write-Host ""
    Write-Host "  ____         __ _   __  __          _ _       " -ForegroundColor Magenta
    Write-Host " / ___|  ___  / _| |_|  \/  | ___  __| (_) __ _ " -ForegroundColor Magenta
    Write-Host " \___ \ / _ \| |_| __| |\/| |/ _ \/ _  | |/ _  |" -ForegroundColor Magenta
    Write-Host "  ___) | (_) |  _| |_| |  | |  __/ (_| | | (_| |" -ForegroundColor Magenta
    Write-Host " |____/ \___/|_|  \__|_|  |_|\___|\__,_|_|\__,_|" -ForegroundColor Magenta
    Write-Host ""
    Write-Host "            Setup Script v1.0                   " -ForegroundColor DarkGray
    Write-Host ""
    
    # Check prerequisites
    if (-not $SkipPrerequisites) {
        $prereqOk = Install-Prerequisites
        if (-not $prereqOk) {
            Write-Host ""
            Write-Error "Prerequisites check failed. Please install missing dependencies and try again."
            Write-Host ""
            exit 1
        }
    }
    else {
        Write-Info "Skipping prerequisite checks"
    }
    
    # Install server dependencies
    $serverOk = Install-ServerDependencies
    if (-not $serverOk) {
        Write-Error "Server setup failed"
        exit 1
    }
    
    # Install client dependencies
    $clientOk = Install-ClientDependencies
    if (-not $clientOk) {
        Write-Error "Client setup failed"
        exit 1
    }
    
    # Summary
    Write-Header "Setup Complete!"
    Write-Host ""
    Write-Host "  To start the server:" -ForegroundColor White
    Write-Host "    cd src\SoftMedia.Server" -ForegroundColor Gray
    Write-Host "    dotnet run" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  To start the client:" -ForegroundColor White
    Write-Host "    cd src\SoftMedia.Client" -ForegroundColor Gray
    Write-Host "    npm run dev" -ForegroundColor Gray
    Write-Host ""
    Write-Host "  Or run this script with -StartServices to start both:" -ForegroundColor White
    Write-Host "    .\setup.ps1 -StartServices" -ForegroundColor Gray
    Write-Host ""
    
    if ($StartServices) {
        Start-SoftMediaServices
    }
}

# Run main
Main
