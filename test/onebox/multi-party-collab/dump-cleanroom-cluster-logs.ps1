[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$kubeConfig,

    [Parameter(Mandatory = $false)]
    [string]
    $jobConfig
)


#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

Write-Host "Cleanroom Spark analytics agent logs:"
kubectl logs -l app.kubernetes.io/name=cleanroom-spark-analytics-agent `
    -n cleanroom-spark-analytics-agent `
    --timestamps=true `
    --tail=20000 `
    --all-containers=true `
    --prefix=true `
    --kubeconfig $kubeConfig
Write-Host "--------------------------------------------------------------"

Write-Host "Cleanroom Spark frontend logs:"
kubectl logs -l app.kubernetes.io/name=cleanroom-spark-frontend `
    -n cleanroom-spark-frontend `
    --timestamps=true `
    --tail=20000 `
    --all-containers=true `
    --prefix=true `
    --kubeconfig $kubeConfig
Write-Host "--------------------------------------------------------------"

Write-Host "Spark Operator logs:"
kubectl logs -l app.kubernetes.io/name=spark-operator `
    -n spark-operator `
    --timestamps=true `
    --tail=20000 `
    --kubeconfig $kubeConfig
Write-Host "--------------------------------------------------------------"

Write-Host "Spark Applications Pods:"
kubectl describe pods -n analytics --kubeconfig $kubeConfig
Write-Host "--------------------------------------------------------------"

if (Test-Path $jobConfig) {
    Write-Host "Spark Application jobConfig.json:"
    Get-Content $jobConfig
    Write-Host "--------------------------------------------------------------"
    $jobDetails = Get-Content $jobConfig | ConvertFrom-Json
    $jobIds = $jobDetails.jobIds

    foreach ($jobId in $jobIds) {
        Write-Host "Spark Pod logs for jobId ${jobId}:"
        kubectl logs -l spark-app-name=$jobId `
            -n analytics `
            --all-containers=true `
            --prefix=true `
            --tail=20000 `
            --kubeconfig $kubeConfig
    }
}
else {
    Write-Host "No Spark Application jobConfig.json found."
}
Write-Host "--------------------------------------------------------------"
