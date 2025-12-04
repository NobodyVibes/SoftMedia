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

# 2. Test Image Proxy with a known image
$imageUrl = "https://upload.wikimedia.org/wikipedia/commons/thumb/b/b6/Image_created_with_a_mobile_phone.png/220px-Image_created_with_a_mobile_phone.png"
$proxyUrl = "$baseUrl/image/proxy?url=$([Uri]::EscapeDataString($imageUrl))"

try {
    Write-Host "Testing Image Proxy..."
    $imageResponse = Invoke-WebRequest -Uri $proxyUrl -Headers $headers -Method Get
    if ($imageResponse.StatusCode -eq 200) {
        Write-Host "SUCCESS: Image proxy returned 200 OK."
        Write-Host "Content-Type: $($imageResponse.Headers['Content-Type'])"
        Write-Host "Content-Length: $($imageResponse.Content.Length)"
    }
    else {
        Write-Host "FAILED: Image proxy returned $($imageResponse.StatusCode)"
    }
}
catch {
    Write-Host "FAILED: Image proxy request failed: $_"
}

# 3. Verify Cache File Exists
$hash = [BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::UTF8.GetBytes($imageUrl))).Replace("-", "").ToLower()
$cachePath = "c:\Users\Admin\Documents\coding2\SoftMedia\src\SoftMedia.Server\cache\images\$hash.png"

if (Test-Path $cachePath) {
    Write-Host "SUCCESS: Cache file found at $cachePath"
}
else {
    Write-Host "FAILED: Cache file not found at $cachePath"
    # Try checking for .jpg fallback if extension parsing failed
    $cachePathJpg = "c:\Users\Admin\Documents\coding2\SoftMedia\src\SoftMedia.Server\cache\images\$hash.jpg"
    if (Test-Path $cachePathJpg) {
        Write-Host "SUCCESS: Cache file found at $cachePathJpg (fallback extension)"
    }
}
