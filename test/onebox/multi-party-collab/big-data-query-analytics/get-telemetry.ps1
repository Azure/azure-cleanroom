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
Write-Output "Verifying logs for cleanroom-spark-analytics-agent...."

Get-LokiLogs `
    -query '{service_name="cleanroom-spark-analytics-agent"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/logs_cleanroom-spark-analytics-agent.json

Write-Output "Verifying traces for cleanroom-spark-analytics-agent...."

Get-TempoTraces `
    -query 'resource.service.name="cleanroom-spark-analytics-agent"' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/traces_cleanroom-spark-analytics-agent.json

Write-Output "Verifying metrics for cleanroom-spark-frontend...."

Get-PrometheusMetrics `
    -query 'http_requests_total{job="cleanroom-spark-frontend"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/metrics_cleanroom-spark-frontend.json

Write-Output "Verifying logs for cleanroom-spark-frontend...."

Get-LokiLogs `
    -query '{service_name="cleanroom-spark-frontend"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/logs_cleanroom-spark-frontend.json

Write-Output "Verifying traces for cleanroom-spark-frontend...."

Get-TempoTraces `
    -query 'resource.service.name="cleanroom-spark-frontend"' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/traces_cleanroom-spark-frontend.json

Write-Output "Verifying logs for spark driver...."

Get-LokiLogs `
    -query '{service_name=~".*-driver"} | spark_role=`driver`' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/logs_spark-driver.json

Write-Output "Verifying traces for spark driver...."

Get-TempoTraces `
    -query 'resource.service.name=~".*-driver" && resource.spark.role="driver"' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/traces_spark-driver.json

Write-Output "Verifying metrics for spark drivers...."
Get-PrometheusMetrics `
    -query 'spark_driver_code_generator_compilation_count_total{job=~".*-driver"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/metrics_spark-drivers.json

Write-Output "Verifying logs for spark executors...."
Get-LokiLogs `
    -query '{service_name=~".*-executor"} | spark_role=`executor`' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/logs_spark-executors.json

Write-Output "Verifying traces for spark executors...."
Get-TempoTraces `
    -query 'resource.service.name=~".*-executor" && resource.spark.role="executor"' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/traces_spark-executors.json

# The job remains the same for both drivers and executors, as all the metrics are exposed by the
# driver alone.
Write-Output "Verifying metrics for spark executors...."
Get-PrometheusMetrics `
    -query 'spark_executor_memory_usage_bytes{job=~".*-driver"}' `
    -start $start `
    -end $end `
    -outFile $outDir/telemetry/metrics_spark-executors.json
