# Demo safe-pipeline · layer 4/5 — application deployment (simulated, no cloud calls)
$ErrorActionPreference = "Stop"
function Step($msg, $ms) { Write-Output ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $msg); Start-Sleep -Milliseconds $ms }

$image = $env:AURA_PARAM_IMAGE; if (-not $image) { $image = "aura-demo-app:latest" }
$replicas = [int]($env:AURA_PARAM_REPLICAS ?? "2")

Step "deploy: pulling image $image" 900
Step "deploy:   layer sha256:9f1c..e2 (12.4 MB) ... done" 600
Step "deploy:   layer sha256:44ab..71 (38.1 MB) ... done" 800
Step "deploy: rolling out $replicas replicas (maxUnavailable=0)" 500
foreach ($i in 1..$replicas) {
    Step ("deploy: replica {0}/{1} ... created -> readiness probe passed (took {2}ms)" -f $i, $replicas, (Get-Random -Minimum 600 -Maximum 1400)) 1000
}
Step "deploy: switching traffic to revision $($image.Split(':')[1])" 700
Step "deploy: previous revision drained, 0 dropped connections" 600
Step "deploy: rollout complete" 300
exit 0
