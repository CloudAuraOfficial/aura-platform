#!/bin/bash
set -euo pipefail

echo "[EmissionLoad-Azure] Starting execution..."

# Authenticate using injected BYOS service principal credentials
if [ -n "${AZURE_CLIENT_ID:-}" ] && [ -n "${AZURE_CLIENT_SECRET:-}" ] && [ -n "${AZURE_TENANT_ID:-}" ]; then
    echo "[EmissionLoad-Azure] Authenticating with service principal..."
    az login --service-principal \
        -u "$AZURE_CLIENT_ID" \
        -p "$AZURE_CLIENT_SECRET" \
        --tenant "$AZURE_TENANT_ID" \
        --output none

    if [ -n "${AZURE_SUBSCRIPTION_ID:-}" ]; then
        az account set --subscription "$AZURE_SUBSCRIPTION_ID"
    fi
    echo "[EmissionLoad-Azure] Authentication successful."
else
    echo "[EmissionLoad-Azure] WARNING: No BYOS credentials provided, using default credentials."
fi

# Execute the layer command injected by the worker
if [ -n "${AURA_LAYER_COMMAND:-}" ]; then
    echo "[EmissionLoad-Azure] Executing layer command..."
    eval "$AURA_LAYER_COMMAND"
    EXIT_CODE=$?
    echo "[EmissionLoad-Azure] Layer command exited with code: $EXIT_CODE"
    exit $EXIT_CODE
else
    echo "[EmissionLoad-Azure] ERROR: No AURA_LAYER_COMMAND specified."
    exit 1
fi
