#!/usr/bin/env bash
# Self-deploy script — called by the cron-based watcher or manually.
# Runs on the VPS to pull latest code and rebuild containers. After a
# successful health check, writes the deployed SHA to .deployed-sha so
# the watcher knows what's actually live (HEAD alone is not enough since
# commits land directly in this repo from local Claude Code sessions).
#
# Recycle strategy: explicit rm + up of aura_api + aura_worker only.
# Avoids docker compose --force-recreate which uses a rename dance
# (temp-prefix → final-name) that breaks on interrupted prior runs and
# leaves orphaned temp-named containers. Costs ~3-5s of API downtime
# per deploy. Postgres and Redis are never touched.
set -euo pipefail

REPO_DIR="${HOME}/aura-platform"
LOG_FILE="${REPO_DIR}/deploy.log"
DEPLOYED_SHA_FILE="${REPO_DIR}/.deployed-sha"
SERVICES=(aura_api aura_worker)

echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy started" | tee -a "$LOG_FILE"

cd "$REPO_DIR"
git pull origin main 2>&1 | tee -a "$LOG_FILE"
docker compose build "${SERVICES[@]}" 2>&1 | tee -a "$LOG_FILE"

# Tear down the app services explicitly. -fs = force (no prompt) + stop first.
# Postgres + Redis stay up because they're not named here.
docker compose rm -fs "${SERVICES[@]}" 2>&1 | tee -a "$LOG_FILE"
docker compose up -d "${SERVICES[@]}" 2>&1 | tee -a "$LOG_FILE"

sleep 5
if curl -sf http://127.0.0.1:8006/health > /dev/null 2>&1; then
    DEPLOYED_SHA=$(git rev-parse HEAD)
    echo "$DEPLOYED_SHA" > "$DEPLOYED_SHA_FILE"
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy succeeded — health check passed (sha=$DEPLOYED_SHA)" | tee -a "$LOG_FILE"
else
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy WARNING — health check failed" | tee -a "$LOG_FILE"
    exit 1
fi
