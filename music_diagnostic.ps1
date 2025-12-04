$baseUrl = "http://localhost:5011/api/v1"
$username = "admin"
$password = "admin123"

# 1. Login
try {
    $body = @{ username = $username; password = $password } | ConvertTo-Json
    $response = Invoke-RestMethod -Uri "$baseUrl/auth/login" -Method Post -Body $body -ContentType "application/json"
    $token = $response.accessToken
    Write-Host "Token obtained."
}
catch {
    Write-Host "Login failed: $_"
    exit
}

$headers = @{ Authorization = "Bearer $token" }

# 2. Get all libraries
try {
    $libraries = Invoke-RestMethod -Uri "$baseUrl/libraries" -Headers $headers
    Write-Host "`nLibraries found:"
    foreach ($lib in $libraries) {
        Write-Host "  - $($lib.name) (Type: $($lib.type), ID: $($lib.id))"
        Write-Host "    Paths: $($lib.paths -join ', ')"
    }
    
    # Find Music library
    $musicLib = $libraries | Where-Object { $_.type -eq "Music" } | Select-Object -First 1
    
    if ($musicLib) {
        Write-Host "`nFound Music library: $($musicLib.name)"
        Write-Host "Library ID: $($musicLib.id)"
        
        # 3. Trigger scan
        Write-Host "`nTriggering scan..."
        $scanResponse = Invoke-RestMethod -Uri "$baseUrl/libraries/$($musicLib.id)/scan" -Method Post -Headers $headers
        Write-Host "Scan response: $($scanResponse.message)"
        
        # Wait a bit for scan to complete
        Write-Host "Waiting 5 seconds for scan to complete..."
        Start-Sleep -Seconds 5
        
        # 4. Get library items
        Write-Host "`nFetching library items..."
        $itemsResponse = Invoke-RestMethod -Uri "$baseUrl/libraries/$($musicLib.id)/items" -Headers $headers
        Write-Host "Total items found: $($itemsResponse.totalCount)"
        
        if ($itemsResponse.items.Count -gt 0) {
            Write-Host "`nFirst 5 items:"
            foreach ($item in ($itemsResponse.items | Select-Object -First 5)) {
                Write-Host "  - $($item.title) ($($item.container))"
            }
        }
        else {
            Write-Host "`nNO ITEMS FOUND. Possible issues:"
            Write-Host "  1. Files are not recognized audio formats"
            Write-Host "  2. Directory doesn't exist or is inaccessible"
            Write-Host "  3. Scanner encountered an error"
            Write-Host "`nCheck backend logs for more details."
        }
    }
    else {
        Write-Host "`nNo Music library found. Available library types:"
        $libraries | ForEach-Object { Write-Host "  - $($_.type)" }
    }
}
catch {
    Write-Host "Error: $_"
}
