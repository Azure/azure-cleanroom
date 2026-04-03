[CmdletBinding()]
param
(
    [int]$TailLines = 20000,
    [int]$MaxRetries = 3,
    [int]$RetryDelaySeconds = 2
)


#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

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

$sandbox_common = "$PSScriptRoot/sandbox_common"

if (-not (Test-Path $sandbox_common/cl-cluster.json)) {
    Write-Host "Not collecting any cleanroom cluster logs as configuration file not found at $sandbox_common/cl-cluster.json"
    return
}

$clCluster = Get-Content $sandbox_common/cl-cluster.json | ConvertFrom-Json
$clClusterName = $clCluster.name
$infraType = $clCluster.infraType

$kubeConfig = "${sandbox_common}/k8s-credentials.yaml"
az cleanroom cluster get-kubeconfig `
    --name $clClusterName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json `
    -f $kubeConfig

Write-Host "Cleanroom Spark analytics agent logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=cleanroom-spark-analytics-agent -n cleanroom-spark-analytics-agent --timestamps=true --tail=$TailLines --all-containers=true --prefix=true --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

Write-Host "Cleanroom Spark frontend logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=cleanroom-spark-frontend -n cleanroom-spark-frontend --timestamps=true --all-containers=true --prefix=true --tail=$TailLines --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

Write-Host "Spark Operator logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=spark-operator -n spark-operator --timestamps=true --tail=$TailLines --kubeconfig $kubeConfig"

Write-Host "--------------------------------------------------------------"

Write-Host "Spark Applications Pods:"
Invoke-KubectlWithRetry "describe pods -n analytics --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

if (Test-Path $sandbox_common/jobConfig.json) {
    Write-Host "Spark Application jobConfig.json:"
    Get-Content $sandbox_common/jobConfig.json
    Write-Host "--------------------------------------------------------------"
    $jobDetails = Get-Content $sandbox_common/jobConfig.json | ConvertFrom-Json
    $jobId = $jobDetails.jobId

    Write-Host "Spark Pod logs:"
    Invoke-KubectlWithRetry "logs -l spark-app-name=$jobId -n analytics --all-containers=true --prefix=true --tail=$TailLines --kubeconfig $kubeConfig"
}
else {
    Write-Host "No Spark Application jobConfig.json found."
}
Write-Host "--------------------------------------------------------------"

Write-Host "KServe inferencing agent logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=kserve-inferencing-agent -n kserve-inferencing-agent --timestamps=true --tail=$TailLines --all-containers=true --prefix=true --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"

Write-Host "KServe inferencing frontend logs:"
Invoke-KubectlWithRetry "logs -l app.kubernetes.io/name=kserve-inferencing-frontend -n kserve-inferencing-frontend --timestamps=true --all-containers=true --prefix=true --tail=$TailLines --kubeconfig $kubeConfig"
Write-Host "--------------------------------------------------------------"
