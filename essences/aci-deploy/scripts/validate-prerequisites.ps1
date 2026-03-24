# Validate Prerequisites — Verify Azure CLI auth, resource group, and ACR exist
# Fails fast if anything is misconfigured.

$ErrorActionPreference = "Stop"

$resourceGroup = $env:AURA_PARAM_RESOURCEGROUP
$acrName       = $env:AURA_PARAM_ACRNAME
$location      = $env:AURA_PARAM_LOCATION

if (-not $resourceGroup) { Write-Error "resourceGroup parameter required"; exit 1 }
if (-not $acrName)       { Write-Error "acrName parameter required"; exit 1 }
if (-not $location)      { Write-Error "location parameter required"; exit 1 }

# 1. Verify Azure CLI is authenticated
Write-Output "Checking Azure CLI authentication..."
$account = az account show --output json 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Azure CLI not authenticated. Run 'az login' or configure a service principal."
    exit 1
}
$accountObj = $account | ConvertFrom-Json
Write-Output "  Subscription: $($accountObj.name) ($($accountObj.id))"
Write-Output "  Tenant: $($accountObj.tenantId)"

# 2. Verify resource group exists
Write-Output ""
Write-Output "Checking resource group '$resourceGroup'..."
$rg = az group show --name $resourceGroup --output json 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Output "  Resource group not found. Creating in $location..."
    az group create --name $resourceGroup --location $location --output none
    if ($LASTEXITCODE -ne 0) { Write-Error "Failed to create resource group"; exit 1 }
    Write-Output "  Created resource group '$resourceGroup'"
} else {
    $rgObj = $rg | ConvertFrom-Json
    Write-Output "  Exists in $($rgObj.location)"
}

# 3. Verify ACR exists
Write-Output ""
Write-Output "Checking container registry '$acrName'..."
$acr = az acr show --name $acrName --output json 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "ACR '$acrName' not found. Create it first: az acr create --name $acrName --resource-group $resourceGroup --sku Basic"
    exit 1
}
$acrObj = $acr | ConvertFrom-Json
Write-Output "  Login server: $($acrObj.loginServer)"
Write-Output "  SKU: $($acrObj.sku.name)"

# 4. Verify Docker is available
Write-Output ""
Write-Output "Checking Docker..."
$dockerVersion = docker --version 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker is not available"
    exit 1
}
Write-Output "  $dockerVersion"

Write-Output ""
Write-Output "All prerequisites validated."
