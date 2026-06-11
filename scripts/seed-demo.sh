#!/usr/bin/env bash
# seed-demo.sh — one-command demo-ready tenant (WI-3, epic #1).
#
# On a CLEAN database: bootstraps a demo tenant + admin.
# On an EXISTING database: logs in with the provided admin credentials
# and seeds the demo assets into that tenant (idempotent — existing
# objects are reused by name/label, never duplicated).
#
# Seeds:
#   1. Cloud account  "Local Simulation (demo)"  — dummy creds, never used
#   2. Essence        "Demo — Safe Pipeline"     — 5 local pwsh layers
#   3. Deployment     "Demo Pipeline (safe)"
#   4. $DEMO_RUNS completed runs (default 2) so history pages aren't empty
#
# Env:
#   AURA_URL        (default http://127.0.0.1:8006)
#   DEMO_TENANT     (default "Aura Demo")            — bootstrap only
#   DEMO_EMAIL      (default demo@aura.local)
#   DEMO_PASSWORD   (default Demo12345$!)            — must meet complexity
#   DEMO_RUNS       (default 2)
set -euo pipefail

AURA_URL="${AURA_URL:-http://127.0.0.1:8006}"
DEMO_TENANT="${DEMO_TENANT:-Aura Demo}"
DEMO_EMAIL="${DEMO_EMAIL:-demo@aura.local}"
DEMO_PASSWORD="${DEMO_PASSWORD:-Demo12345\$!}"
DEMO_RUNS="${DEMO_RUNS:-2}"
REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ESSENCE_FILE="${REPO_DIR}/Essences/Demo/safe-pipeline/Essencefile.json"

say()  { echo "[seed-demo] $*"; }
fail() { echo "[seed-demo] ERROR: $*" >&2; exit 1; }

# jq-free JSON field extraction (python3 is on every target host)
jget() { python3 -c "import json,sys; d=json.load(sys.stdin); print(d$1 if d$1 is not None else '')" 2>/dev/null || true; }

api() { # api METHOD PATH [JSON_BODY]
    local method="$1" path="$2" body="${3:-}"
    if [[ -n "$body" ]]; then
        curl -s -X "$method" "${AURA_URL}${path}" \
            -H "Authorization: Bearer ${TOKEN:-}" -H "Content-Type: application/json" \
            -d "$body"
    else
        curl -s -X "$method" "${AURA_URL}${path}" \
            -H "Authorization: Bearer ${TOKEN:-}" -H "Content-Type: application/json"
    fi
}

[[ -f "$ESSENCE_FILE" ]] || fail "essence file not found: $ESSENCE_FILE"
curl -sf "${AURA_URL}/health" > /dev/null || fail "API not reachable at ${AURA_URL}"

# ── 1. Bootstrap or login ─────────────────────────────────────────────
say "bootstrap attempt (tenant '${DEMO_TENANT}')…"
BOOT=$(curl -s -X POST "${AURA_URL}/api/v1/auth/bootstrap" -H "Content-Type: application/json" \
    -d "{\"email\":\"${DEMO_EMAIL}\",\"password\":\"${DEMO_PASSWORD}\",\"tenantName\":\"${DEMO_TENANT}\"}")
if [[ "$(echo "$BOOT" | jget "['error']")" == "conflict" ]]; then
    say "platform already bootstrapped — logging in as ${DEMO_EMAIL}"
else
    say "bootstrapped fresh tenant '${DEMO_TENANT}' with admin ${DEMO_EMAIL}"
fi

LOGIN=$(curl -s -X POST "${AURA_URL}/api/v1/auth/login" -H "Content-Type: application/json" \
    -d "{\"email\":\"${DEMO_EMAIL}\",\"password\":\"${DEMO_PASSWORD}\"}")
TOKEN=$(echo "$LOGIN" | jget "['token']")
[[ -n "$TOKEN" ]] || fail "login failed for ${DEMO_EMAIL}: $LOGIN
  (on an existing DB, pass that tenant's admin via DEMO_EMAIL/DEMO_PASSWORD)"
say "authenticated"

# ── 2. Cloud account (dummy — the safe essence never reads creds) ─────
ACCOUNT_LABEL="Local Simulation (demo)"
ACCOUNTS=$(api GET "/api/v1/cloud-accounts?limit=100")
ACCOUNT_ID=$(echo "$ACCOUNTS" | python3 -c "
import json,sys
d=json.load(sys.stdin)
for a in d.get('items', []):
    if a.get('label') == '${ACCOUNT_LABEL}':
        print(a['id']); break")
if [[ -z "$ACCOUNT_ID" ]]; then
    CREATED=$(api POST "/api/v1/cloud-accounts" \
        "{\"provider\":\"Azure\",\"label\":\"${ACCOUNT_LABEL}\",\"credentials\":\"{\\\"note\\\":\\\"simulation only — not real credentials\\\"}\"}")
    ACCOUNT_ID=$(echo "$CREATED" | jget "['id']")
    [[ -n "$ACCOUNT_ID" ]] || fail "cloud account creation failed: $CREATED"
    say "created cloud account '${ACCOUNT_LABEL}'"
else
    say "cloud account '${ACCOUNT_LABEL}' already present"
fi

# ── 3. Essence ────────────────────────────────────────────────────────
ESSENCE_NAME="Demo — Safe Pipeline"
ESSENCES=$(api GET "/api/v1/essences?limit=100")
ESSENCE_ID=$(echo "$ESSENCES" | python3 -c "
import json,sys
d=json.load(sys.stdin)
for e in d.get('items', []):
    if e.get('name') == '${ESSENCE_NAME}':
        print(e['id']); break")
if [[ -z "$ESSENCE_ID" ]]; then
    BODY=$(python3 - "$ESSENCE_FILE" "$ACCOUNT_ID" << 'EOF'
import json, sys
essence = open(sys.argv[1]).read()
print(json.dumps({
    "name": "Demo — Safe Pipeline",
    "cloudAccountId": sys.argv[2],
    "essenceJson": essence,
}))
EOF
)
    CREATED=$(api POST "/api/v1/essences" "$BODY")
    ESSENCE_ID=$(echo "$CREATED" | jget "['id']")
    [[ -n "$ESSENCE_ID" ]] || fail "essence creation failed: $CREATED"
    say "created essence '${ESSENCE_NAME}'"
else
    say "essence '${ESSENCE_NAME}' already present"
fi

# ── 4. Deployment ─────────────────────────────────────────────────────
DEPLOYMENT_NAME="Demo Pipeline (safe)"
DEPLOYMENTS=$(api GET "/api/v1/deployments?limit=100")
DEPLOYMENT_ID=$(echo "$DEPLOYMENTS" | python3 -c "
import json,sys
d=json.load(sys.stdin)
for x in d.get('items', []):
    if x.get('name') == '${DEPLOYMENT_NAME}':
        print(x['id']); break")
if [[ -z "$DEPLOYMENT_ID" ]]; then
    CREATED=$(api POST "/api/v1/deployments" \
        "{\"essenceId\":\"${ESSENCE_ID}\",\"name\":\"${DEPLOYMENT_NAME}\",\"isEnabled\":true}")
    DEPLOYMENT_ID=$(echo "$CREATED" | jget "['id']")
    [[ -n "$DEPLOYMENT_ID" ]] || fail "deployment creation failed: $CREATED"
    say "created deployment '${DEPLOYMENT_NAME}'"
else
    say "deployment '${DEPLOYMENT_NAME}' already present"
fi

# ── 5. Seed run history ───────────────────────────────────────────────
for i in $(seq 1 "$DEMO_RUNS"); do
    RUN=$(api POST "/api/v1/deployments/${DEPLOYMENT_ID}/runs")
    RUN_ID=$(echo "$RUN" | jget "['id']")
    [[ -n "$RUN_ID" ]] || fail "run trigger failed: $RUN"
    say "run ${i}/${DEMO_RUNS} triggered (${RUN_ID}) — waiting…"
    for _ in $(seq 1 60); do
        sleep 3
        STATUS=$(api GET "/api/v1/deployments/${DEPLOYMENT_ID}/runs/${RUN_ID}" | jget "['status']")
        case "$STATUS" in
            Succeeded) say "run ${i}/${DEMO_RUNS} ✓ Succeeded"; break ;;
            Failed|Cancelled) fail "run ${i} ended ${STATUS} — check /dashboard/run?deploymentId=${DEPLOYMENT_ID}&runId=${RUN_ID}" ;;
        esac
    done
    [[ "$STATUS" == "Succeeded" ]] || fail "run ${i} did not complete within 180s (status: ${STATUS:-unknown})"
done

say ""
say "demo seed complete ✓"
say "  dashboard:   ${AURA_URL}/dashboard  (login: ${DEMO_EMAIL})"
say "  deployment:  ${DEPLOYMENT_NAME} — click Run to open the Run Theater"
