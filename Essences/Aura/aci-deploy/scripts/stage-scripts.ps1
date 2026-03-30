# Stage Scripts — Copy executor scripts and IaC templates into the run workdir
# This layer runs first to make all subsequent scripts available.

$ErrorActionPreference = "Stop"

$repoRoot = $env:AURA_PARAM_REPOROOT
if (-not $repoRoot) { Write-Error "AURA_PARAM_REPOROOT is required"; exit 1 }

$workDir = Get-Location
Write-Output "Staging scripts from $repoRoot into $workDir"

# Copy executor scripts
$sourceScripts = Join-Path $repoRoot "essences/aci-deploy/scripts"
if (-not (Test-Path $sourceScripts)) {
    Write-Error "Script source not found: $sourceScripts"
    exit 1
}

Copy-Item -Path "$sourceScripts/*" -Destination "$workDir/scripts/" -Recurse -Force
Write-Output "  Copied executor scripts"

# Copy Bicep template
$infraDir = Join-Path $workDir "infra/azure"
New-Item -ItemType Directory -Path $infraDir -Force | Out-Null
Copy-Item -Path (Join-Path $repoRoot "infra/azure/main.bicep") -Destination $infraDir -Force
Write-Output "  Copied Bicep template"

# Verify staged files
$staged = Get-ChildItem -Path $workDir -Recurse -File | Select-Object -ExpandProperty Name
Write-Output ""
Write-Output "Staged files:"
$staged | ForEach-Object { Write-Output "  $_" }

Write-Output ""
Write-Output "Stage complete."
