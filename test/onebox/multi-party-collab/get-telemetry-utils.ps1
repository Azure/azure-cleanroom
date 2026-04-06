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

    $maxRetries = 3
    $retryCount = 0
    $metricsRequest = $null

    while ($retryCount -lt $maxRetries) {
        try {
            $metricsRequest = curl -G -s "$prometheusEndpoint/api/v1/query_range" `
                --data-urlencode "query=$query" `
                --data-urlencode "start=$start" `
                --data-urlencode "end=$end" `
                --data-urlencode "step=30s" | jq | ConvertFrom-Json
        }
        catch {
            $retryCount++
            if ($retryCount -lt $maxRetries) {
                $waitTime = [math]::Pow(2, $retryCount)
                Write-Output "Metrics fetch attempt $retryCount failed with error: $($_.Exception.Message), retrying in $waitTime seconds..."
                Start-Sleep -Seconds $waitTime
                continue
            }
            else {
                throw
            }
        }

        if ($metricsRequest.status -eq "success" -and $metricsRequest.data.result.metric.Count -gt 0) {
            break
        }

        $retryCount++
        if ($retryCount -lt $maxRetries) {
            $waitTime = [math]::Pow(2, $retryCount)
            Write-Output "Metrics fetch attempt $retryCount returned unsuccessful status, retrying in $waitTime seconds..."
            Start-Sleep -Seconds $waitTime
        }
    }

    if ($metricsRequest.status -ne "success" -or $metricsRequest.data.result.metric.Count -eq 0) {
        throw "Failed to fetch metrics from prometheus endpoint after $maxRetries attempts: $($metricsRequest | ConvertTo-Json -Depth 100)"
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

    $maxRetries = 3
    $retryCount = 0
    $logsRequest = $null

    while ($retryCount -lt $maxRetries) {
        try {
            $logsRequest = curl -G -s "$lokiEndpoint/loki/api/v1/query_range" `
                --data-urlencode "query=$query" `
                --data-urlencode "start=$start" `
                --data-urlencode "end=$end" `
                --data-urlencode "limit=10" | jq | ConvertFrom-Json
        }
        catch {
            $retryCount++
            if ($retryCount -lt $maxRetries) {
                $waitTime = [math]::Pow(2, $retryCount)
                Write-Output "Logs fetch attempt $retryCount failed with error: $($_.Exception.Message), retrying in $waitTime seconds..."
                Start-Sleep -Seconds $waitTime
                continue
            }
            else {
                throw
            }
        }

        if ($logsRequest.status -eq "success" -and $logsRequest.data.result.Count -gt 0) {
            break
        }

        $retryCount++
        if ($retryCount -lt $maxRetries) {
            $waitTime = [math]::Pow(2, $retryCount)
            Write-Output "Logs fetch attempt $retryCount returned unsuccessful status, retrying in $waitTime seconds..."
            Start-Sleep -Seconds $waitTime
        }
    }

    if ($logsRequest.status -ne "success" -or $logsRequest.data.result.Count -eq 0) {
        throw "Failed to fetch logs for spark frontend from loki endpoint after $maxRetries attempts: $($logsRequest | ConvertTo-Json -Depth 100)"
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

    $maxRetries = 3
    $retryCount = 0
    $tracesRequest = $null

    while ($retryCount -lt $maxRetries) {
        try {
            $tracesRequest = curl -G -s "$tempoEndpoint/api/search" `
                --data-urlencode $query `
                --data-urlencode "start=$start" `
                --data-urlencode "end=$end" `
                --data-urlencode "limit=10" | jq | ConvertFrom-Json
        }
        catch {
            $retryCount++
            if ($retryCount -lt $maxRetries) {
                $waitTime = [math]::Pow(2, $retryCount)
                Write-Output "Traces fetch attempt $retryCount failed with error: $($_.Exception.Message), retrying in $waitTime seconds..."
                Start-Sleep -Seconds $waitTime
                continue
            }
            else {
                throw
            }
        }

        if ($tracesRequest.traces.Count -gt 0) {
            break
        }

        $retryCount++
        if ($retryCount -lt $maxRetries) {
            $waitTime = [math]::Pow(2, $retryCount)
            Write-Output "Traces fetch attempt $retryCount returned no traces, retrying in $waitTime seconds..."
            Start-Sleep -Seconds $waitTime
        }
    }

    if ($tracesRequest.traces.Count -eq 0) {
        throw "Failed to fetch traces from tempo endpoint after $maxRetries attempts: $($tempoEndpoint | ConvertTo-Json -Depth 100)"
    }

    $tracesRequest | ConvertTo-Json -Depth 100 | Out-File -FilePath $outFile
}
