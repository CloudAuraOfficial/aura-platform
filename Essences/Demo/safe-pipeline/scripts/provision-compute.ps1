# Demo safe-pipeline · layer 3/5 — compute provisioning (simulated, no cloud calls)
$ErrorActionPreference = "Stop"
function Step($msg, $ms) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg); Start-Sleep -Milliseconds $ms }

$count = [int]($env:AURA_PARAM_INSTANCES ?? "2")
$size = $env:AURA_PARAM_SIZE; if (-not $size) { $size = "Standard_B2s" }

Step "compute: requesting $count x $size instances" 600
foreach ($i in 1..$count) {
    Step ("compute: vm-demo-{0} .... accepted (correlation {1})" -f $i, [guid]::NewGuid().ToString().Substring(0,8)) 700
    Step ("compute: vm-demo-{0} .... PowerState/starting" -f $i) 1100
    Step ("compute: vm-demo-{0} .... PowerState/running · nic attached to snet-demo-{0}" -f $i) 900
}
Step "compute: registering instances with load balancer lb-demo" 800
Step "compute: health probes green ($count/$count instances)" 700
Step "compute: provisioning complete" 300
exit 0
