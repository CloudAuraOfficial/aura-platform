#!/bin/bash
set -euo pipefail

echo "[EmissionLoad-AWS] Starting execution..."

# Authenticate using injected BYOS credentials
if [ -n "${AWS_ACCESS_KEY_ID:-}" ] && [ -n "${AWS_SECRET_ACCESS_KEY:-}" ]; then
    echo "[EmissionLoad-AWS] AWS credentials detected."
    if [ -n "${AWS_DEFAULT_REGION:-}" ]; then
        echo "[EmissionLoad-AWS] Region: $AWS_DEFAULT_REGION"
    fi
else
    echo "[EmissionLoad-AWS] WARNING: No BYOS credentials provided, using default credentials."
fi

# Execute the layer command injected by the worker
if [ -n "${AURA_LAYER_COMMAND:-}" ]; then
    echo "[EmissionLoad-AWS] Executing layer command..."
    eval "$AURA_LAYER_COMMAND"
    EXIT_CODE=$?
    echo "[EmissionLoad-AWS] Layer command exited with code: $EXIT_CODE"
    exit $EXIT_CODE
else
    echo "[EmissionLoad-AWS] ERROR: No AURA_LAYER_COMMAND specified."
    exit 1
fi
