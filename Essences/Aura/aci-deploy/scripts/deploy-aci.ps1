# Deploy ACI — Run Bicep deployment to create/update the container group
# Secrets (JWT_SECRET, ENCRYPTION_KEY, POSTGRES_PASSWORD) must be set as environment
# variables — they come from the CloudAccount credentials, not from parameters.

$ErrorActionPreference = "Stop"

$resourceGroup = $env:AURA_PARAM_RESOURCEGROUP
$acrName       = $env:AURA_PARAM_ACRNAME
$imageTag      = $env:AURA_PARAM_IMAGETAG
$location      = $env:AURA_PARAM_LOCATION
$dnsLabel      = $env:AURA_PARAM_DNSLABEL
$bicepPath     = $env:AURA_PARAM_BICEPPATH
$apiCpu        = $env:AURA_PARAM_APICPU
$apiMemory     = $env:AURA_PARAM_APIMEMORY

# Secrets from env (injected by worker from CloudAccount credentials)
$jwtSecret        = $env:JWT_SECRET
$encryptionKey    = $env:ENCRYPTION_KEY
$postgresPassword = $env:POSTGRES_PASSWORD

# Validate required
if (-not $resourceGroup) { Write-Error "resourceGroup required"; exit 1 }
if (-not $acrName)       { Write-Error "acrName required"; exit 1 }
if (-not $location)      { Write-Error "location required"; exit 1 }
if (-not $dnsLabel)      { Write-Error "dnsLabel required"; exit 1 }

# Defaults
if (-not $imageTag)  { $imageTag = "latest" }
if (-not $apiCpu)    { $apiCpu = "0.5" }
if (-not $apiMemory) { $apiMemory = "1.0" }
if (-not $bicepPath) { $bicepPath = "infra/azure/main.bicep" }

# Resolve Bicep path relative to workdir
$workDir = Get-Location
$fullBicepPath = Join-Path $workDir $bicepPath
if (-not (Test-Path $fullBicepPath)) {
    Write-Error "Bicep template not found: $fullBicepPath"
    exit 1
}

# Validate secrets
if (-not $jwtSecret)        { Write-Error "JWT_SECRET not set. Configure in CloudAccount credentials."; exit 1 }
if (-not $encryptionKey)    { Write-Error "ENCRYPTION_KEY not set. Configure in CloudAccount credentials."; exit 1 }
if (-not $postgresPassword) { Write-Error "POSTGRES_PASSWORD not set. Configure in CloudAccount credentials."; exit 1 }

Write-Output "Deploying to Azure Container Instances"
Write-Output "  Resource Group: $resourceGroup"
Write-Output "  ACR: $acrName"
Write-Output "  Image Tag: $imageTag"
Write-Output "  DNS Label: $dnsLabel"
Write-Output "  CPU/Memory: ${apiCpu} cores / ${apiMemory} GB"
Write-Output "  Bicep: $fullBicepPath"
Write-Output ""

# Run Bicep deployment
az deployment group create `
    --resource-group $resourceGroup `
    --template-file $fullBicepPath `
    --parameters `
        acrName=$acrName `
        imageTag=$imageTag `
        location=$location `
        dnsLabel=$dnsLabel `
        apiCpu=$apiCpu `
        apiMemory=$apiMemory `
        jwtSecret=$jwtSecret `
        encryptionKey=$encryptionKey `
        postgresPassword=$postgresPassword `
    --output table

if ($LASTEXITCODE -ne 0) {
    Write-Error "Bicep deployment failed"
    exit 1
}

# Output deployment result
Write-Output ""
$fqdn = "${dnsLabel}.${location}.azurecontainer.io"
Write-Output "Deployment complete."
Write-Output "  FQDN: $fqdn"
Write-Output "  Health: http://${fqdn}:8000/health"
Write-Output "  Dashboard: http://${fqdn}:8000/dashboard/login"
