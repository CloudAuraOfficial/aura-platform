# Demo safe-pipeline · layer 1/5 — preflight validation (simulated, no cloud calls)
$ErrorActionPreference = "Stop"
function Step($msg, $ms) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg); Start-Sleep -Milliseconds $ms }

$env:AURA_PARAM_TARGETENV | Out-Null
Step "preflight: resolving target environment '$($env:AURA_PARAM_TARGETENV)'" 400
Step "preflight: checking executor runtime ........ pwsh $($PSVersionTable.PSVersion) OK" 500
Step "preflight: validating essence snapshot ...... 5 layers, DAG acyclic OK" 600
Step "preflight: checking credential envelope ..... sealed (AES-256-GCM) OK" 500
Step "preflight: quota check ....................... 2/10 vCPU, 0/3 networks OK" 700
Step "preflight: all checks passed — clear for provisioning" 300
exit 0
