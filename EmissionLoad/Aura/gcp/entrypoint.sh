#!/bin/bash
set -euo pipefail

echo "[EmissionLoad-GCP] Starting execution..."

# Authenticate using injected BYOS service account key
if [ -n "${GOOGLE_APPLICATION_CREDENTIALS_JSON:-}" ]; then
    echo "[EmissionLoad-GCP] Authenticating with service account..."
    echo "$GOOGLE_APPLICATION_CREDENTIALS_JSON" > /tmp/gcp-key.json
    gcloud auth activate-service-account --key-file=/tmp/gcp-key.json --quiet
    rm -f /tmp/gcp-key.json

    if [ -n "${GCP_PROJECT_ID:-}" ]; then
        gcloud config set project "$GCP_PROJECT_ID" --quiet
    fi
    echo "[EmissionLoad-GCP] Authentication successful."
else
    echo "[EmissionLoad-GCP] WARNING: No BYOS credentials provided, using default credentials."
fi

# Execute the layer command injected by the worker
if [ -n "${AURA_LAYER_COMMAND:-}" ]; then
    echo "[EmissionLoad-GCP] Executing layer command..."
    eval "$AURA_LAYER_COMMAND"
    EXIT_CODE=$?
    echo "[EmissionLoad-GCP] Layer command exited with code: $EXIT_CODE"
    exit $EXIT_CODE
else
    echo "[EmissionLoad-GCP] ERROR: No AURA_LAYER_COMMAND specified."
    exit 1
fi
