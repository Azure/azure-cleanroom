[CmdletBinding()]
param (
    [string]$outDir = "$PSScriptRoot/generated",
    [string]$deploymentConfigDir = "$PSScriptRoot/../../workloads/generated"
)

$root = git rev-parse --show-toplevel

. $root/test/onebox/multi-party-collab/get-telemetry-utils.ps1

$end = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$start = ([DateTimeOffset]::UtcNow.AddHours(-1)).ToUnixTimeSeconds()

$kubeConfig = "$deploymentConfigDir/cl-cluster/k8s-credentials.yaml"
Get-Job -Command "*kubectl proxy --port 8484*" | Stop-Job
Get-Job -Command "*kubectl proxy --port 8484*" | Remove-Job
kubectl proxy --port 8484 --kubeconfig $kubeConfig &

mkdir -p $outDir/telemetry
Write-Output "Verifying logs for kserve-inferencing-agent...."

Get-LokiLogs `
    -query '{service_name="kserve-inferencing-agent"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/logs_kserve-inferencing-agent.json

Write-Output "Verifying traces for kserve-inferencing-agent...."

Get-TempoTraces `
    -query 'resource.service.name="kserve-inferencing-agent"' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/traces_kserve-inferencing-agent.json

Write-Output "Verifying metrics for kserve-inferencing-frontend...."

Get-PrometheusMetrics `
    -query 'http_requests_total{job="kserve-inferencing-frontend"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/metrics_kserve-inferencing-frontend.json

Write-Output "Verifying logs for kserve-inferencing-frontend...."

Get-LokiLogs `
    -query '{service_name="kserve-inferencing-frontend"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/logs_kserve-inferencing-frontend.json

Write-Output "Verifying traces for kserve-inferencing-frontend...."

Get-TempoTraces `
    -query 'resource.service.name="kserve-inferencing-frontend"' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/traces_kserve-inferencing-frontend.json
