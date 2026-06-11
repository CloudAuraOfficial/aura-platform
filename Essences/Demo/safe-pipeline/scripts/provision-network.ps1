# Demo safe-pipeline · layer 2/5 — network provisioning (simulated, no cloud calls)
$ErrorActionPreference = "Stop"
function Step($msg, $ms) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg); Start-Sleep -Milliseconds $ms }

$cidr = $env:AURA_PARAM_CIDR; if (-not $cidr) { $cidr = "10.42.0.0/16" }
$subnets = [int]($env:AURA_PARAM_SUBNETS ?? "3")

Step "network: creating virtual network vnet-demo ($cidr)" 900
foreach ($i in 1..$subnets) {
    Step ("network:   subnet snet-demo-{0} (10.42.{0}.0/24) ...... allocated ({1}ms)" -f $i, (Get-Random -Minimum 180 -Maximum 420)) 800
}
Step "network: attaching network security group nsg-demo" 700
Step "network:   rule allow-https (443/tcp, priority 100) .. applied" 500
Step "network:   rule deny-all-inbound (priority 4096) ...... applied" 500
Step "network: route table rt-demo associated to $subnets subnets" 600
Step "network: provisioning complete — vnet-demo ready" 400
exit 0
