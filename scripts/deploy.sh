#!/usr/bin/env bash
# Self-deploy script — called by the cron-based watcher or manually.
# Runs on the VPS to pull latest code and rebuild containers. After a
# successful health check, writes the deployed SHA to .deployed-sha so
# the watcher knows what's actually live (HEAD alone is not enough since
# commits land directly in this repo from local Claude Code sessions).
set -euo pipefail

REPO_DIR="${HOME}/aura-platform"
LOG_FILE="${REPO_DIR}/deploy.log"
DEPLOYED_SHA_FILE="${REPO_DIR}/.deployed-sha"

echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy started" | tee -a "$LOG_FILE"

cd "$REPO_DIR"
git pull origin main 2>&1 | tee -a "$LOG_FILE"
docker compose build 2>&1 | tee -a "$LOG_FILE"
docker compose up -d --force-recreate aura_api aura_worker 2>&1 | tee -a "$LOG_FILE"

sleep 5
if curl -sf http://127.0.0.1:8006/health > /dev/null 2>&1; then
    DEPLOYED_SHA=$(git rev-parse HEAD)
    echo "$DEPLOYED_SHA" > "$DEPLOYED_SHA_FILE"
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy succeeded — health check passed (sha=$DEPLOYED_SHA)" | tee -a "$LOG_FILE"
else
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy WARNING — health check failed" | tee -a "$LOG_FILE"
    exit 1
fi
