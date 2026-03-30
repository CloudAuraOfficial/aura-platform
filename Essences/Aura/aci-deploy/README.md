# Essence: Aura Platform — Azure ACI Deployment

Deploy the Aura Platform (API + PostgreSQL + Redis) to Azure Container Instances
using the Aura Platform's own orchestration engine.

## Layers

```
StageScripts ──► ValidatePrerequisites ──► BuildAndPushImages ──► DeployContainerGroup ──► HealthCheck ──► SmokeTest
```

| # | Layer | Executor | Purpose |
|---|-------|----------|---------|
| 0 | StageScripts | PowerShell | Copy scripts + Bicep into the run workdir |
| 1 | ValidatePrerequisites | PowerShell | Verify Azure CLI auth, resource group, ACR |
| 2 | BuildAndPushImages | PowerShell | Build API image, push to ACR, import base images |
| 3 | DeployContainerGroup | PowerShell | Run Bicep deployment to create ACI container group |
| 4 | HealthCheck | Python | Poll `/health` until responding (120s timeout) |
| 5 | SmokeTest | Python | Verify `/health`, `/metrics`, auth gate, dashboard |

## Prerequisites

- Azure CLI authenticated (`az login` or service principal)
- Docker installed and running
- Azure Container Registry exists
- CloudAccount configured in Aura Platform with:
  - `JWT_SECRET`
  - `ENCRYPTION_KEY`
  - `POSTGRES_PASSWORD`

## Usage

### Via API

```bash
# 1. Create CloudAccount with Azure credentials
POST /api/v1/cloud-accounts
{
  "provider": "Azure",
  "label": "Production",
  "credentials": "{\"JWT_SECRET\":\"...\",\"ENCRYPTION_KEY\":\"...\",\"POSTGRES_PASSWORD\":\"...\"}"
}

# 2. Create Essence (paste contents of essence.json)
POST /api/v1/essences
{
  "name": "Aura ACI Deploy",
  "cloudAccountId": "<cloud-account-id>",
  "essenceJson": "<contents of essence.json>"
}

# 3. Create Deployment
POST /api/v1/deployments
{
  "essenceId": "<essence-id>",
  "name": "Deploy to ACI",
  "isEnabled": true
}

# 4. Trigger a run
POST /api/v1/deployments/<deployment-id>/runs
```

### Parameters to customize

Edit `essence.json` before creating:

| Parameter | Default | Description |
|-----------|---------|-------------|
| `resourceGroup` | `cloudaura-rg` | Azure resource group |
| `acrName` | `cloudauraregistry` | Azure Container Registry name |
| `location` | `eastus` | Azure region |
| `dnsLabel` | `cloudaura-api` | Public DNS label |
| `imageTag` | `latest` | Docker image tag |
| `apiCpu` | `0.5` | CPU cores for API container |
| `apiMemory` | `1.0` | Memory (GB) for API container |
