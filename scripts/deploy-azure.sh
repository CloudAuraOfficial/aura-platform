#!/usr/bin/env bash
# Aura Platform — Azure Deployment Script
#
# Prerequisites:
#   - Azure CLI authenticated (az login or service principal)
#   - .env.azure file with required variables (see .env.azure.example)
#   - Docker authenticated to ACR (az acr login --name <acr>)
#
# Usage:
#   ./scripts/deploy-azure.sh              # Full deploy (build + push + deploy)
#   ./scripts/deploy-azure.sh --deploy-only # Skip build, redeploy existing image
#   ./scripts/deploy-azure.sh --teardown    # Delete all Azure resources

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Load Azure env vars
ENV_FILE="${PROJECT_ROOT}/.env.azure"
if [[ ! -f "$ENV_FILE" ]]; then
    echo "ERROR: .env.azure not found. Copy .env.azure.example and fill in values."
    exit 1
fi
source "$ENV_FILE"

# Required variables
: "${AZURE_RESOURCE_GROUP:?Set AZURE_RESOURCE_GROUP in .env.azure}"
: "${AZURE_ACR_NAME:?Set AZURE_ACR_NAME in .env.azure}"
: "${AZURE_LOCATION:?Set AZURE_LOCATION in .env.azure}"
: "${AZURE_DNS_LABEL:?Set AZURE_DNS_LABEL in .env.azure}"
: "${JWT_SECRET:?Set JWT_SECRET in .env.azure}"
: "${ENCRYPTION_KEY:?Set ENCRYPTION_KEY in .env.azure}"
: "${POSTGRES_PASSWORD:?Set POSTGRES_PASSWORD in .env.azure}"

IMAGE_TAG="${IMAGE_TAG:-latest}"
ACR_LOGIN_SERVER="${AZURE_ACR_NAME}.azurecr.io"

# --- Functions ---

build_and_push() {
    echo "==> Authenticating Docker to ACR..."
    az acr login --name "$AZURE_ACR_NAME"

    echo "==> Building API image..."
    docker build -t "${ACR_LOGIN_SERVER}/aura-api:${IMAGE_TAG}" \
        --target api "$PROJECT_ROOT"

    echo "==> Pushing to ACR..."
    docker push "${ACR_LOGIN_SERVER}/aura-api:${IMAGE_TAG}"

    echo "==> Ensuring base images exist in ACR..."
    az acr import --name "$AZURE_ACR_NAME" \
        --source docker.io/library/postgres:16-alpine \
        --image postgres:16-alpine --force 2>/dev/null || true
    az acr import --name "$AZURE_ACR_NAME" \
        --source docker.io/library/redis:7-alpine \
        --image redis:7-alpine --force 2>/dev/null || true

    echo "==> Images pushed successfully."
}

deploy() {
    echo "==> Deploying to Azure Container Instances..."
    az deployment group create \
        --resource-group "$AZURE_RESOURCE_GROUP" \
        --template-file "${PROJECT_ROOT}/infra/azure/main.bicep" \
        --parameters \
            acrName="$AZURE_ACR_NAME" \
            imageTag="$IMAGE_TAG" \
            location="$AZURE_LOCATION" \
            dnsLabel="$AZURE_DNS_LABEL" \
            jwtSecret="$JWT_SECRET" \
            encryptionKey="$ENCRYPTION_KEY" \
            postgresPassword="$POSTGRES_PASSWORD" \
        --output table

    echo ""
    echo "==> Deployment complete. Waiting for containers to start..."
    sleep 15

    # Smoke test
    FQDN="${AZURE_DNS_LABEL}.${AZURE_LOCATION}.azurecontainer.io"
    echo "==> Smoke testing http://${FQDN}:8000/health ..."

    for i in {1..6}; do
        if curl -sf "http://${FQDN}:8000/health" >/dev/null 2>&1; then
            echo "✅ Health check passed!"
            echo ""
            echo "Dashboard: http://${FQDN}:8000/dashboard/login"
            echo "Health:    http://${FQDN}:8000/health"
            echo "Metrics:   http://${FQDN}:8000/metrics"
            return 0
        fi
        echo "   Attempt $i/6 — waiting 10s..."
        sleep 10
    done

    echo "⚠️  Health check did not pass after 60s. Check container logs:"
    echo "   az container logs --resource-group $AZURE_RESOURCE_GROUP --name aura-api --container-name aura-api"
    return 1
}

teardown() {
    echo "==> Tearing down Azure resources..."
    read -p "Delete container group 'aura-api' in '$AZURE_RESOURCE_GROUP'? [y/N] " confirm
    if [[ "$confirm" =~ ^[Yy]$ ]]; then
        az container delete --resource-group "$AZURE_RESOURCE_GROUP" --name aura-api --yes
        echo "✅ Container group deleted."
    else
        echo "Cancelled."
    fi
}

status() {
    echo "==> Container status:"
    az container show --resource-group "$AZURE_RESOURCE_GROUP" --name aura-api \
        --query "containers[].{name:name, state:instanceView.currentState.state, restarts:instanceView.restartCount}" \
        --output table 2>/dev/null || echo "No container group found."
}

# --- Main ---

case "${1:-}" in
    --deploy-only)
        deploy
        ;;
    --teardown)
        teardown
        ;;
    --status)
        status
        ;;
    --build-only)
        build_and_push
        ;;
    *)
        build_and_push
        deploy
        ;;
esac
