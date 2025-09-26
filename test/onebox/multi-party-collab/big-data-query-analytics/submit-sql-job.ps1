[CmdletBinding()]
param (
    [string]$outDir = "$PSScriptRoot/generated"
)

function Get-TimeStamp {
    return "[{0:MM/dd/yy} {0:HH:mm:ss}]" -f (Get-Date)
}


function Invoke-SqlJobAndWait {
    param (
        [string]$queryDocumentId,
        [string]$analyticsEndpoint,
        [string]$cgsClient,
        [string]$outDir,
        [string]$kubeConfig,
        [Nullable[DateTimeOffset]]$startDate = $null,
        [Nullable[DateTimeOffset]]$endDate = $null
    )

    $token = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $cgsClient)
    $script:submissionJson = $null
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false

        # Additional Local-Authorization header support is added in agent as kubectl proxy command drops Authorization header.
        $runId = (New-Guid).ToString().Substring(0, 8)
        $body = @{ runId = $runId }
        if ($startDate) { $body.startDate = $startDate }
        if ($endDate) { $body.endDate = $endDate }

        $script:submissionJson = curl -k -s --fail-with-body -X POST "${analyticsEndpoint}/queries/$queryDocumentId/run" `
            -H "content-type: application/json" `
            -H "Local-Authorization: Bearer $token" `
            -d ($body | ConvertTo-Json -Compress)

        if ($LASTEXITCODE -ne 0) {
            Write-Output $script:submissionJson | jq
            throw "/queries/$queryDocumentId/run failed. Check the output above for details."
        }
    }

    $submissionResult = $script:submissionJson | ConvertFrom-Json
    $jobId = $submissionResult.id
    $jobConfig = "$outDir/jobConfig.json"
    if (Test-Path $jobConfig) {
        $jobIds = (Get-Content $jobConfig | ConvertFrom-Json).jobIds
    }
    else {
        $jobIds = @()
    }
    $jobIds += $jobId
    $jobIdList = $jobIds -join '","'
    @"
{
    "jobIds": ["$jobIdList"]
}
"@ > $outDir/jobConfig.json

    $applicationTimeout = New-TimeSpan -Minutes 30
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    Write-Output "Waiting for job execution to complete..."
    $jobStatus = $null
    do {
        Write-Host "$(Get-TimeStamp) Checking status of job: $jobId"

        $token = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $cgsClient)
        $script:jobStatusResponse = ""
        & {
            # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
            $PSNativeCommandUseErrorActionPreference = $false
            $script:jobStatusResponse = $(curl -k -s --fail-with-body -X GET "${analyticsEndpoint}/status/$jobId" `
                    -H "Local-Authorization: Bearer $token")
            if ($LASTEXITCODE -ne 0) {
                $script:jobStatusResponse | jq
                throw "/status/$jobId failed. Check the output above for details."
            }
        }

        $script:jobStatusResponse | jq
        $jobStatus = $script:jobStatusResponse | ConvertFrom-Json

        if ($jobStatus.status.applicationState.state -eq "COMPLETED") {
            Write-Host -ForegroundColor Green "$(Get-TimeStamp) Application has completed execution."
            Write-Output "Checking that Spark driver pod '$($jobStatus.status.driverInfo.podName)' exited gracefully..."
            pwsh $PSScriptRoot/wait-for-spark-driver-pod-termination.ps1 `
                -podName $jobStatus.status.driverInfo.podName `
                -namespace "analytics" `
                -kubeConfig $kubeConfig
            $allExecutorsTerminated = $true
            $executorState = $jobStatus.status.executorState
            foreach ($podName in $executorState.PSObject.Properties.Name) {
                $state = $executorState.$podName
                Write-Host "Executor pod: $podName, Reported state: $state"
                if ($state -ne "COMPLETED" -and $state -ne "FAILED") {
                    Write-Host -ForegroundColor Red "Pod '$podName' is not TERMINATED"
                    $allExecutorsTerminated = $false
                }

                if ($state -eq "FAILED") {
                    Write-Host -ForegroundColor Red "Executor pod '$podName' has FAILED state...."
                }
            }

            if ($allExecutorsTerminated) {
                Write-Output "All executor pods are TERMINATED."
                break
            }

            if ($stopwatch.elapsed -gt $applicationTimeout) {
                Write-Host -ForegroundColor Red "One or more executor pods failed or are in an incorrect state."
                throw "Hit timeout waiting for one or more executor pods to report COMPLETED state."
            }
        }
        
        if ($jobStatus.status.applicationState.state -eq "FAILED") {
            Write-Host -ForegroundColor Red "$(Get-TimeStamp) Application has failed execution."
            throw "Application has failed execution."
        }

        Write-Host "Application $jobId state is: $($jobStatus.status.applicationState.state)"
    
        if ($stopwatch.elapsed -gt $applicationTimeout) {
            throw "Hit timeout waiting for application $jobId to complete execution."
        }
        
        Write-Host "Waiting for 15 seconds before checking status again..."
        Start-Sleep -Seconds 15
    } while ($true)
}

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$jobConfig = Get-Content -Path "$outDir/submitSqlJobConfig.json" | ConvertFrom-Json
$queryDocumentId = $jobConfig.queryDocumentId
$s3QueryDocumentId = $jobConfig.s3QueryDocumentId
$cgsClient = $jobConfig.cgsClient
$maliciousQueryDocumentId = $jobConfig.maliciousQueryDocumentId
rm -f "$outDir/jobConfig.json"

$kubeConfig = "$outDir/cl-cluster/k8s-credentials.yaml"
Get-Job -Command "*kubectl proxy --port 8181*" | Stop-Job
Get-Job -Command "*kubectl proxy --port 8181*" | Remove-Job
kubectl proxy --port 8181 --kubeconfig $kubeConfig &
$analyticsEndpoint = "http://localhost:8181/api/v1/namespaces/cleanroom-spark-analytics-agent/services/https:cleanroom-spark-analytics-agent:443/proxy"

$timeout = New-TimeSpan -Minutes 1
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -o /dev/null -w "%{http_code}" -k -s ${analyticsEndpoint}/ready) -ne "200") {
        Write-Output "Waiting for analytics endpoint to be ready at ${analyticsEndpoint}/ready"
        Start-Sleep -Seconds 3
        if ($stopwatch.elapsed -gt $timeout) {
            # Re-run the command once to log its output.
            curl -k -s ${analyticsEndpoint}/ready
            throw "Hit timeout waiting for analytics endpoint to be ready."
        }
    }
}

Write-Output "Executing query '$queryDocumentId'."
# This query has no start/end dates
Invoke-SqlJobAndWait `
    -queryDocumentId $queryDocumentId `
    -analyticsEndpoint $analyticsEndpoint `
    -cgsClient $cgsClient `
    -outDir $outDir `
    -kubeConfig $kubeConfig

Write-Output "Executing S3 query '$s3QueryDocumentId'."
$currentDate = [DateTimeOffset]"2025-09-01"
# This query has start/end dates
Invoke-SqlJobAndWait `
    -queryDocumentId $s3QueryDocumentId `
    -analyticsEndpoint $analyticsEndpoint `
    -cgsClient $cgsClient `
    -outDir $outDir `
    -kubeConfig $kubeConfig `
    -startDate $currentDate `
    -endDate $currentDate.AddDays(1)

Write-Output "Executing malicious query '$maliciousQueryDocumentId'."

$token = (az cleanroom governance client get-access-token --query accessToken -o tsv --name $cgsClient)
$script:submissionJson = $null
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false

    $expectedErrorCode = "QueryMissingApprovalsFromDatasetOwners"
    # Additional Local-Authorization header support is added in agent as kubectl proxy command drops Authorization header.
    $runId = (New-Guid).ToString().Substring(0, 8)
    $script:submissionJson = curl -k -s --fail-with-body -X POST "${analyticsEndpoint}/queries/$maliciousQueryDocumentId/run" `
        -H "content-type: application/json" `
        -H "Local-Authorization: Bearer $token" `
        -d @"
{
    "runId": "$runId"
}
"@
    if ($LASTEXITCODE -eq 0) {
        throw "Expected malicious query execution to fail with error code '$expectedErrorCode', but it succeeded."
    }

    Write-Output $script:submissionJson | jq
    $errorCode = $script:submissionJson | jq '.error.code' | tr -d '"'
    if ($errorCode -ne $expectedErrorCode) {
        throw "Expected error code '$expectedErrorCode' but got '$errorCode'."
    }
    Write-Output "malicious query execution failed as expected with error code '$expectedErrorCode'."
}