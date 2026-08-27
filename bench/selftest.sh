#!/usr/bin/env bash
# Acceptance checks for the harness (fail = non-zero exit):
#  1. same seed → identical provider call count (deterministic)
#  2. retry WITHOUT idempotency key under ack-loss double-provisions; WITH key it never does
set -euo pipefail; cd "$(dirname "$0")"
m() { ./run_cell.sh "$1" | tail -1; }
j() { python3 -c "import sys,json;print(json.load(sys.stdin)['$1'])"; }
a=$(m '{"variant":"bounded","n":4,"jobs":50,"fail":0.2,"retry":"naive","seed":3}' | j provider_calls)
b=$(m '{"variant":"bounded","n":4,"jobs":50,"fail":0.2,"retry":"naive","seed":3}' | j provider_calls)
[ "$a" = "$b" ] || { echo "FAIL determinism: $a != $b"; exit 1; }
noidem=$(m '{"variant":"bounded","n":4,"jobs":100,"ackloss":0.1,"retry":"naive","idem":false,"seed":3}' | j double_provisions)
idem=$(m   '{"variant":"bounded","n":4,"jobs":100,"ackloss":0.1,"retry":"naive","idem":true,"seed":3}'  | j double_provisions)
[ "$noidem" -gt 0 ] || { echo "FAIL: expected double-provisions without idempotency key, got $noidem"; exit 1; }
[ "$idem" -eq 0 ]   || { echo "FAIL: idempotency key leaked $idem double-provisions"; exit 1; }
echo "OK deterministic (calls=$a) · no-key dup=$noidem · with-key dup=$idem"
