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
    # Tag the just-built images with the git SHA so rollback.sh can swap back to a
    # previous known-good image instantly (no rebuild). Keep the last 3 SHA tags per
    # service; prune older. Local-only — ghcr off-box push is a later enhancement
    # (needs a PAT with write:packages). ponytail: local tags cover single-VPS rollback.
    for svc in aura_api aura_worker; do
        img="aura-platform-${svc}"
        if docker image inspect "$img:latest" >/dev/null 2>&1; then
            docker tag "$img:latest" "$img:$DEPLOYED_SHA" 2>/dev/null || true
            # keep newest 3 sha tags (exclude :latest), remove the rest
            docker images "$img" --format '{{.CreatedAt}}\t{{.Tag}}' \
                | grep -vP '\tlatest$' | sort -r | tail -n +4 | cut -f2 \
                | while read -r old; do docker rmi "$img:$old" 2>/dev/null || true; done
        fi
    done
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy succeeded — health check passed (sha=$DEPLOYED_SHA)" | tee -a "$LOG_FILE"
else
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') Deploy WARNING — health check failed" | tee -a "$LOG_FILE"
    exit 1
fi
