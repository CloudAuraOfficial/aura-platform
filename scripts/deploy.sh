#!/usr/bin/env bash
# Self-deploy script — called by the deploy webhook endpoint or manually.
# Runs on the VPS to pull latest code and rebuild containers.
set -euo pipefail

REPO_DIR="${HOME}/aura-platform"
LOG_FILE="${REPO_DIR}/deploy.log"

echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy started" | tee -a "$LOG_FILE"

cd "$REPO_DIR"
git pull origin main 2>&1 | tee -a "$LOG_FILE"
docker compose build 2>&1 | tee -a "$LOG_FILE"
docker compose up -d 2>&1 | tee -a "$LOG_FILE"

sleep 5
if curl -sf http://127.0.0.1:8006/health > /dev/null 2>&1; then
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy succeeded — health check passed" | tee -a "$LOG_FILE"
else
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy WARNING — health check failed" | tee -a "$LOG_FILE"
fi
