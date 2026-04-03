[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$kubeConfig,

    [Parameter(Mandatory = $false)]
    [string]
    $jobConfig,

    [int]$TailLines = 20000,
    [int]$MaxRetries = 3,
    [int]$RetryDelaySeconds = 2
)


#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Helper function to retry kubectl commands
function Invoke-KubectlWithRetry {
    param(
        [string]$Command,
        [int]$MaxRetries = $script:MaxRetries,
        [int]$InitialDelaySeconds = $script:RetryDelaySeconds
    )
    
    $attempt = 0
    $delay = $InitialDelaySeconds
    
    while ($attempt -lt $MaxRetries) {
        try {
            Write-Verbose "Executing: kubectl $Command"
            $result = Invoke-Expression "kubectl $Command 2>&1"
            return $result
        }
        catch {
            $attempt++
            if ($attempt -ge $MaxRetries) {
                Write-Error "Failed after $MaxRetries attempts: $_"
                throw
            }
            Write-Warning "Attempt $attempt failed: $_. Retrying in $delay seconds..."
            Start-Sleep -Seconds $delay
            $delay *= 2  # Exponential backoff
        }
    }
}

Write-Host "Cleanroom Spark analytics agent logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=cleanroom-spark-analytics-agent -n cleanroom-spark-analytics-agent --timestamps=true --tail=$TailLines --all-containers=true --prefix=true --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

Write-Host "Cleanroom Spark frontend logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=cleanroom-spark-frontend -n cleanroom-spark-frontend --timestamps=true --tail=$TailLines --all-containers=true --prefix=true --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

Write-Host "Spark Operator logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=spark-operator -n spark-operator --timestamps=true --tail=$TailLines --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

Write-Host "Spark Applications Pods:"
Invoke-KubectlWithRetry "describe pods -n analytics --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

if (Test-Path $jobConfig) {
    Write-Host "Spark Application jobConfig.json:"
    Get-Content $jobConfig
    Write-Host "--------------------------------------------------------------"
    $jobDetails = Get-Content $jobConfig | ConvertFrom-Json
    $jobIds = $jobDetails.jobIds

    foreach ($jobId in $jobIds) {
        Write-Host "Spark Pod logs for jobId ${jobId}:"
        Invoke-KubectlWithRetry "logs -l spark-app-name=$jobId -n analytics --all-containers=true --prefix=true --tail=$TailLines --kubeconfig $kubeConfig"
    }
}
else {
    Write-Host "No Spark Application jobConfig.json found."
}
Write-Host "--------------------------------------------------------------"