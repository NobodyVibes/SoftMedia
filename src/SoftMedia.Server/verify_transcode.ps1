$baseUrl = "http://127.0.0.1:5011/api/v1"
$transcodeUrl = "http://127.0.0.1:5011/api/transcode"

# 1. Login
Write-Host "Logging in..."
$loginBody = @{
    username = "admin"
    password = "admin123"
} | ConvertTo-Json

$loginResponse = Invoke-RestMethod -Uri "$baseUrl/auth/login" -Method Post -Body $loginBody -ContentType "application/json"
$token = $loginResponse.accessToken
Write-Host "Token obtained."

$headers = @{
    Authorization = "Bearer $token"
}

# 2. Get Libraries
Write-Host "Getting libraries..."
$libraries = Invoke-RestMethod -Uri "$baseUrl/libraries" -Method Get -Headers $headers

if ($libraries.Count -eq 0) {
    Write-Host "No libraries found. Cannot proceed."
    exit
}

$libId = $libraries[0].id
Write-Host "Using Library ID: $libId"

# 3. Get Items
Write-Host "Getting items..."
$itemsResponse = Invoke-RestMethod -Uri "$baseUrl/libraries/$libId/items" -Method Get -Headers $headers
$items = $itemsResponse.items

if ($items.Count -eq 0) {
    Write-Host "No items found in library. Cannot proceed."
    exit
}

$mediaId = $items[0].id
Write-Host "Using Media ID: $mediaId"

# 4. Request Transcode
Write-Host "Requesting Transcode Playlist..."
try {
    $playlistUrl = "$transcodeUrl/$mediaId/master.m3u8"
    $response = Invoke-WebRequest -Uri $playlistUrl -Method Get -Headers $headers
    
    if ($response.StatusCode -eq 200) {
        Write-Host "Transcode request successful!"
        Write-Host "Playlist content length: $($response.Content.Length)"
        Write-Host "Content Type: $($response.Headers['Content-Type'])"
    } else {
        Write-Host "Transcode request failed with status: $($response.StatusCode)"
    }
} catch {
    Write-Host "Error requesting transcode: $_"
    Write-Host "Response: $($_.Exception.Response)"
}
