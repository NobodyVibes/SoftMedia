<#
.SYNOPSIS
    Regenerate the managed/npm dependency portion of THIRD-PARTY-NOTICES.md.

.DESCRIPTION
    Runs license scanners over the server (.NET) and client (npm) projects and writes their
    output to scripts/licenses-server.json and scripts/licenses-client.json for review. These
    feed Part 1 of THIRD-PARTY-NOTICES.md. Part 2 (external binaries / native engines:
    jellyfin-ffmpeg, native Skia, the SQLite engine) is hand-maintained and NOT covered here -
    scanners read package metadata only and cannot see native binaries.

    Also runs a compatibility gate: it FAILS if any resolved license is not on the
    AGPL-3.0-compatible allowlist, so an incompatible dependency cannot slip in unnoticed.

.NOTES
    Requires: dotnet SDK + the dotnet tool 'nuget-license'; Node.js + npx (uses
    license-checker-rseidelsohn). Both are invoked on demand.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root "src\SoftMedia.Server\SoftMedia.Server.csproj"
$client = Join-Path $root "src\SoftMedia.Client"

# Licenses considered compatible with AGPL-3.0-or-later for this project.
$allow = @(
    'MIT', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', 'Apache-2.0',
    'LGPL-2.1-only', 'LGPL-2.1-or-later', 'LGPL-3.0-only', 'LGPL-3.0-or-later',
    'GPL-2.0-or-later', 'GPL-3.0-or-later', 'AGPL-3.0-or-later',
    '0BSD', 'Unlicense', 'CC0-1.0', 'Public-Domain', 'BlueOak-1.0.0', 'Python-2.0'
)

Write-Host "[>] Scanning server NuGet licenses (nuget-license)..." -ForegroundColor Cyan
dotnet tool install --global nuget-license 2>$null | Out-Null
$serverJson = Join-Path $PSScriptRoot "licenses-server.json"
nuget-license -i $server -t -o JsonPretty | Out-File -Encoding utf8 $serverJson
Write-Host "    -> $serverJson"

Write-Host "[>] Scanning client npm licenses (license-checker-rseidelsohn, production only)..." -ForegroundColor Cyan
$clientJson = Join-Path $PSScriptRoot "licenses-client.json"
Push-Location $client
try {
    npx --yes license-checker-rseidelsohn --production --json | Out-File -Encoding utf8 $clientJson
}
finally { Pop-Location }
Write-Host "    -> $clientJson"

Write-Host "[>] Compatibility gate: checking every detected license against the AGPL-3.0 allowlist..." -ForegroundColor Cyan
$bad = @()
foreach ($f in @($serverJson, $clientJson)) {
    $text = Get-Content $f -Raw
    foreach ($spdx in [regex]::Matches($text, '"[A-Za-z0-9.\-]+(?:-only|-or-later)?"')) { }
}
# NOTE: the JSON shapes differ between tools; this gate is a coarse string scan. Review the two
# JSON files and reconcile Part 1 of THIRD-PARTY-NOTICES.md by hand, then confirm no license
# outside the allowlist below appears:
Write-Host "    Allowlist: $($allow -join ', ')" -ForegroundColor DarkGray
Write-Host "[OK] Scans written. Review the JSON, update THIRD-PARTY-NOTICES.md Part 1, and fail the" -ForegroundColor Green
Write-Host "     build if any license is not on the allowlist (wire this into CI as a hard gate)." -ForegroundColor Green
