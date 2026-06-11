# Demo safe-pipeline · layer 5/5 — smoke test (simulated, no cloud calls)
$ErrorActionPreference = "Stop"
function Step($msg, $ms) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg); Start-Sleep -Milliseconds $ms }

$endpoints = [int]($env:AURA_PARAM_ENDPOINTS ?? "4")
$paths = @("/health", "/api/v1/status", "/api/v1/version", "/metrics")

Step "smoke: running $endpoints endpoint checks against https://demo.internal" 500
foreach ($i in 0..($endpoints - 1)) {
    $p = $paths[$i % $paths.Count]
    Step ("smoke:   GET {0} -> 200 ({1}ms)" -f $p, (Get-Random -Minimum 8 -Maximum 95)) 600
}
Step "smoke: p95 latency 87ms (budget 250ms) ........ PASS" 500
Step "smoke: error rate 0.00% (budget 1%) ........... PASS" 500
Step "smoke: all checks green — pipeline succeeded" 400
Write-Output ""
Write-Output "=== DEMO PIPELINE COMPLETE — no real cloud resources were created ==="
exit 0
