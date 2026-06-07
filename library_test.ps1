$baseUrl = "http://127.0.0.1:5011/api/v1"

# Login to get token
$loginBody = @{ username = "admin"; password = "admin123" } | ConvertTo-Json
try {
    $loginRes = Invoke-RestMethod -Uri "$baseUrl/auth/login" -Method Post -ContentType "application/json" -Body $loginBody
    $token = $loginRes.accessToken
    $headers = @{ Authorization = "Bearer $token" }
    Write-Host "Token obtained."
}
catch {
    Write-Host "Login failed: $_"
    exit
}

# 1. Create Library
$libBody = @{ name = "Test Lib"; type = "Movie"; paths = @("C:\Users\Admin\Documents\coding2\SoftMedia\media\movies") } | ConvertTo-Json
try {
    $lib = Invoke-RestMethod -Uri "$baseUrl/libraries" -Method Post -ContentType "application/json" -Headers $headers -Body $libBody
    Write-Host "Library Created: $($lib.id)"
}
catch {
    $stream = $_.Exception.Response.GetResponseStream()
    $reader = New-Object System.IO.StreamReader($stream)
    $responseBody = $reader.ReadToEnd()
    Write-Host "Failed to create library: $_"
    Write-Host "Response Body: $responseBody"
    exit
}

# 2. Duplicate Path Check
$dupBody = @{ name = "Dup Lib"; type = "Movie"; paths = @("C:\Users\Admin\Documents\coding2\SoftMedia\media\movies") } | ConvertTo-Json
try {
    Invoke-RestMethod -Uri "$baseUrl/libraries" -Method Post -ContentType "application/json" -Headers $headers -Body $dupBody
    Write-Host "ERROR: Duplicate library created (should have failed)."
}
catch {
    if ($_.Exception.Response.StatusCode -eq "BadRequest") {
        Write-Host "SUCCESS: Duplicate path rejected."
    }
    else {
        Write-Host "Failed with unexpected error: $_"
    }
}

# 3. Scan Library
try {
    Invoke-RestMethod -Uri "$baseUrl/libraries/$($lib.id)/scan" -Method Post -Headers $headers
    Write-Host "Scan initiated."
}
catch {
    Write-Host "Failed to scan: $_"
}

# 4. Delete Library
try {
    Invoke-RestMethod -Uri "$baseUrl/libraries/$($lib.id)" -Method Delete -Headers $headers
    Write-Host "Library Deleted."
}
catch {
    Write-Host "Failed to delete: $_"
}
