# Build and Push Images — Build API image and ensure base images exist in ACR

$ErrorActionPreference = "Stop"

$acrName  = $env:AURA_PARAM_ACRNAME
$imageTag = $env:AURA_PARAM_IMAGETAG
$repoRoot = $env:AURA_PARAM_REPOROOT

if (-not $acrName)  { Write-Error "acrName parameter required"; exit 1 }
if (-not $imageTag) { $imageTag = "latest" }
if (-not $repoRoot) { Write-Error "repoRoot parameter required"; exit 1 }

$loginServer = "$acrName.azurecr.io"

# 1. Authenticate Docker to ACR
Write-Output "Authenticating Docker to ACR '$acrName'..."
az acr login --name $acrName
if ($LASTEXITCODE -ne 0) { Write-Error "ACR login failed"; exit 1 }

# 2. Build API image
$fullImage = "${loginServer}/aura-api:${imageTag}"
Write-Output ""
Write-Output "Building API image: $fullImage"
docker build -t $fullImage --target api $repoRoot
if ($LASTEXITCODE -ne 0) { Write-Error "Docker build failed"; exit 1 }

# 3. Push API image
Write-Output ""
Write-Output "Pushing API image to ACR..."
docker push $fullImage
if ($LASTEXITCODE -ne 0) { Write-Error "Docker push failed"; exit 1 }
Write-Output "  Pushed: $fullImage"

# 4. Import base images (postgres, redis) — idempotent
Write-Output ""
Write-Output "Ensuring base images exist in ACR..."

Write-Output "  Importing postgres:16-alpine..."
az acr import --name $acrName `
    --source docker.io/library/postgres:16-alpine `
    --image postgres:16-alpine --force 2>&1 | Out-Null

Write-Output "  Importing redis:7-alpine..."
az acr import --name $acrName `
    --source docker.io/library/redis:7-alpine `
    --image redis:7-alpine --force 2>&1 | Out-Null

# 5. Verify images
Write-Output ""
Write-Output "Images in ACR:"
az acr repository list --name $acrName --output table
if ($LASTEXITCODE -ne 0) { Write-Error "Failed to list ACR repositories"; exit 1 }

Write-Output ""
Write-Output "Build and push complete."
