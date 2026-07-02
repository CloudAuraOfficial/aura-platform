#!/usr/bin/env bash
# Fast rollback for aura-platform — swap aura_api + aura_worker to a previously
# built image (tagged by git SHA in deploy.sh), no rebuild. Recreates only the
# app containers; Postgres + Redis are never touched.
#
#   rollback.sh              list available SHA-tagged images (most recent first)
#   rollback.sh <sha>        roll api+worker back to that SHA's images
#
# NOTE: this rolls back the running IMAGES, not git. The deploy watcher compares
# .deployed-sha to git HEAD, so after a rollback you must ALSO revert the code
# (git revert on main) or the watcher will redeploy HEAD within a minute. This
# script writes .deployed-sha = the rolled-back SHA and prints the reminder.
set -euo pipefail
REPO_DIR="${HOME}/aura-platform"
LOG_FILE="${REPO_DIR}/deploy.log"
DEPLOYED_SHA_FILE="${REPO_DIR}/.deployed-sha"
SERVICES=(aura_api aura_worker)
cd "$REPO_DIR"

list() {
    echo "Available rollback images (newest first):"
    docker images "aura-platform-aura_api" --format '{{.CreatedAt}}\t{{.Tag}}' \
        | grep -vP '\tlatest$' | sort -r | cut -f2 | sed 's/^/  /'
}

target="${1:-}"
if [ -z "$target" ]; then list; exit 0; fi

for svc in "${SERVICES[@]}"; do
    img="aura-platform-${svc}"
    docker image inspect "$img:$target" >/dev/null 2>&1 || {
        echo "ERROR: $img:$target not found. Options:"; list; exit 1; }
done

echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') ROLLBACK to sha=$target" | tee -a "$LOG_FILE"
for svc in "${SERVICES[@]}"; do
    img="aura-platform-${svc}"
    docker tag "$img:$target" "$img:latest"
done
docker compose rm -fs "${SERVICES[@]}" 2>&1 | tee -a "$LOG_FILE"
docker compose up -d --no-build "${SERVICES[@]}" 2>&1 | tee -a "$LOG_FILE"

sleep 5
if curl -sf http://127.0.0.1:8006/health >/dev/null 2>&1; then
    echo "$target" > "$DEPLOYED_SHA_FILE"
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') ROLLBACK ok — health passed (sha=$target)" | tee -a "$LOG_FILE"
    echo ">>> REMINDER: revert code on main too (git revert), or the deploy watcher will redeploy HEAD within ~60s."
    command -v ~/rogerclaude/lib/notify.sh >/dev/null 2>&1 && ~/rogerclaude/lib/notify.sh deploy/aura-platform warn "rolled back to $target — revert main or watcher will redeploy HEAD"
else
    echo "$(date -u '+%Y-%m-%dT%H:%M:%SZ') ROLLBACK health check FAILED" | tee -a "$LOG_FILE"; exit 1
fi
