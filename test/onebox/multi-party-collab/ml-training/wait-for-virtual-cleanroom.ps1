[CmdletBinding()]
param
(
    [Parameter(Mandatory)]
    [string]
    $appName,

    [string]
    $cleanroomIp
)

function Get-TimeStamp {
    return "[{0:MM/dd/yy} {0:HH:mm:ss}]" -f (Get-Date)
}

do {
    $applicationStatus = docker exec cleanroom-control-plane curl -s -k https://${cleanroomIp}:8200/gov/$appName/status
    Write-Host "Got application status: $applicationStatus"

    if ($applicationStatus -ne "" -and $null -ne $applicationStatus) {
        $status = $applicationStatus | ConvertFrom-Json
        if ($status.status -contains "exited") {
            Write-Host -ForegroundColor Yellow "$(Get-TimeStamp) Application is terminated. Checking exit code."
            if ($status.exit_code -ne 0) {
                Write-Host -ForegroundColor DarkRed "$(Get-TimeStamp) Application exited with non-zero exit code $($status.exit_code)"
                exit $status.exit_code
            }

            Write-Host -ForegroundColor DarkGreen "$(Get-TimeStamp) Application exited successfully."
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

    Write-Host "Waiting for 30 seconds before checking status again..."
    Start-Sleep -Seconds 30
} while ($true)

