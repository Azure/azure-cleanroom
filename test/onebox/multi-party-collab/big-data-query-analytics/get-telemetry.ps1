[CmdletBinding()]
param (
    [string]$outDir = "$PSScriptRoot/generated"
)

$end = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$start = ([DateTimeOffset]::UtcNow.AddHours(-1)).ToUnixTimeSeconds()

$kubeConfig = "$outDir/cl-cluster/k8s-credentials.yaml"
Get-Job -Command "*kubectl proxy --port 8484*" | Stop-Job
Get-Job -Command "*kubectl proxy --port 8484*" | Remove-Job
kubectl proxy --port 8484 --kubeconfig $kubeConfig &

function Get-PrometheusMetrics {
    param(
        [string] $query,
        [int64] $start,
        [int64] $end,
        [string] $outFile
    )
    $prometheusEndpoint = "http://localhost:8484/api/v1/namespaces/telemetry/services/http:cleanroom-spark-prometheus-server:80/proxy"

    $timeout = New-TimeSpan -Minutes 1
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        while ((curl -o /dev/null -w "%{http_code}" -s ${prometheusEndpoint}/api/v1/query?query=up) -ne "200") {
            Write-Output "Waiting for prometheus endpoint to be ready at ${analyticsEndpoint}/api/v1/query?query=up"
            Start-Sleep -Seconds 3
            if ($stopwatch.elapsed -gt $timeout) {
                # Re-run the command once to log its output.
                curl -s ${prometheusEndpoint}/api/v1/query?query=up
                throw "Hit timeout waiting for prometheus endpoint to be ready."
            }
        }
    }

    $metricsRequest = curl -G -s "$prometheusEndpoint/api/v1/query_range" `
        --data-urlencode "query=$query" `
        --data-urlencode "start=$start" `
        --data-urlencode "end=$end" `
        --data-urlencode "step=30s" | jq | ConvertFrom-Json

    if ($metricsRequest.status -ne "success" -or $metricsRequest.data.result.metric.Count -eq 0) {
        throw "Failed to fetch metrics from prometheus endpoint: $($metricsRequest | ConvertTo-Json -Depth 100)"
    }

    $metricsRequest | ConvertTo-Json -Depth 100 | Out-File -FilePath $outFile
}

function Get-LokiLogs {
    param(
        [string] $query,
        [int64] $start,
        [int64] $end,
        [string] $outFile
    )
    $lokiEndpoint = "http://localhost:8484/api/v1/namespaces/telemetry/services/http:cleanroom-spark-loki:3100/proxy"

    $timeout = New-TimeSpan -Minutes 1
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        while ((curl -o /dev/null -w "%{http_code}" -s ${lokiEndpoint}/ready) -ne "200") {
            Write-Output "Waiting for loki endpoint to be ready at ${lokiEndpoint}/ready"
            Start-Sleep -Seconds 3
            if ($stopwatch.elapsed -gt $timeout) {
                # Re-run the command once to log its output.
                curl -s ${lokiEndpoint}/ready
                throw "Hit timeout waiting for loki endpoint to be ready."
            }
        }
    }


    $logsRequest = curl -G -s "$lokiEndpoint/loki/api/v1/query_range" `
        --data-urlencode "query=$query" `
        --data-urlencode "start=$start" `
        --data-urlencode "end=$end" `
        --data-urlencode "limit=1000" | jq | ConvertFrom-Json

    if ($logsRequest.status -ne "success" -or $logsRequest.data.result.Count -eq 0) {
        throw "Failed to fetch logs for spark frontend from loki endpoint: $($logsRequest | ConvertTo-Json -Depth 100)"
    }

    $logsRequest | ConvertTo-Json -Depth 100 | Out-File -FilePath $outFile
}

function Get-TempoTraces {
    param(
        [string] $query,
        [int64] $start,
        [int64] $end,
        [string] $outFile
    )

    $tempoEndpoint = "http://localhost:8484/api/v1/namespaces/telemetry/services/http:cleanroom-spark-tempo:3200/proxy"

    $timeout = New-TimeSpan -Minutes 1
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        while ((curl -o /dev/null -w "%{http_code}" -s ${tempoEndpoint}/ready) -ne "200") {
            Write-Output "Waiting for tempo endpoint to be ready at ${tempoEndpoint}/ready"
            Start-Sleep -Seconds 3
            if ($stopwatch.elapsed -gt $timeout) {
                # Re-run the command once to log its output.
                curl -s ${tempoEndpoint}/ready
                throw "Hit timeout waiting for tempo endpoint to be ready."
            }
        }
    }

    $tracesRequest = curl -G -s "$tempoEndpoint/api/search" `
        --data-urlencode $query `
        --data-urlencode "start=$start" `
        --data-urlencode "end=$end" `
        --data-urlencode "limit=1000" | jq | ConvertFrom-Json

    if ($tracesRequest.traces.Count -eq 0) {
        throw "Failed to fetch traces from tempo endpoint: $($tempoEndpoint | ConvertTo-Json -Depth 100)"
    }

    $tracesRequest | ConvertTo-Json -Depth 100 | Out-File -FilePath $outFile
}


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
