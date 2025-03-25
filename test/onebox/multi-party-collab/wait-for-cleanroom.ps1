[CmdletBinding()]
param
(
    [Parameter(Mandatory)]
    [string]
    $appName,

    [string]
    $proxyUrl
)

function Get-TimeStamp {
    return "[{0:MM/dd/yy} {0:HH:mm:ss}]" -f (Get-Date)
}

$applicationExecutionTimeout = New-TimeSpan -Minutes 45
$applicationStartTimeout = New-TimeSpan -Minutes 15
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

Write-Host "Using proxy URL: $proxyUrl to talk to the cleanroom at http://ccr.cleanroom.local"
Write-Host "Waiting for application $appName to start execution..."
do {
    $httpCode = curl -o /dev/null -w "%{http_code}" -s http://ccr.cleanroom.local:8200/gov/$appName/status --proxy $proxyUrl
    if ($httpCode -eq "200") {
        Write-Host -ForegroundColor Green "$(Get-TimeStamp) GetStatus API returned HTTP 200. Application is running."
        break
    }

    Write-Host -ForegroundColor DarkRed "$(Get-TimeStamp) GetStatus API failed with HTTP Error code: $httpCode"

    if ($stopwatch.elapsed -gt $applicationStartTimeout) {
        throw "Hit timeout waiting for application to start execution."
    }

    Write-Host "Waiting for 30 seconds before checking status again..."
    Start-Sleep -Seconds 30
} while ($true)


$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
do {
    $applicationStatus = curl -s http://ccr.cleanroom.local:8200/gov/$appName/status --proxy $proxyUrl
    Write-Host "Got application status: $applicationStatus"

    if ($applicationStatus -ne "" -and $null -ne $applicationStatus) {
        $status = $applicationStatus | ConvertFrom-Json
        if ($status.status -contains "exited") {
            Write-Host -ForegroundColor Yellow "$(Get-TimeStamp) Application is terminated. Checking exit code."
            if ($status.exit_code -ne 0) {
                Write-Host -ForegroundColor DarkRed "$(Get-TimeStamp) Application exited with non-zero exit code $($status.exit_code)"
                exit $status.exit_code
            }

            Write-Host -ForegroundColor DarkGreen "$(Get-TimeStamp) Code launcher exited successfully."
            exit 0
        }
        # Container status is 'started' when the container is present but is not running. This is an unexpected state.
        elseif ($status.status -contains "started") {
            Write-Host -ForegroundColor DarkRed "$(Get-TimeStamp) Application is in started state. Only expected states are 'running' and 'exited'"
            exit 1
        }
        elseif ($status.status -contains "running") {
            Write-Host -ForegroundColor Green "$(Get-TimeStamp) Application is running."
        }
        else {
            Write-Host -ForegroundColor Yellow "$(Get-TimeStamp) Application is in unknown state:"
            Write-Host -ForegroundColor Yellow $applicationStatus
            throw "Application is in unknown state."
        }
    }
    else {
        throw "Application container status not found. This is unexpected."
    }

    if ($stopwatch.elapsed -gt $applicationExecutionTimeout) {
        throw "Hit timeout waiting for application to finish execution."
    }

    Write-Host "Waiting for 30 seconds before checking status again..."
    Start-Sleep -Seconds 30
} while ($true)

