[CmdletBinding()]
param
(
)


#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

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
    --all-containers=true `
    --prefix=true `
    --tail=20000 `
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

if (Test-Path $sandbox_common/jobConfig.json) {
    Write-Host "Spark Application jobConfig.json:"
    Get-Content $sandbox_common/jobConfig.json
    Write-Host "--------------------------------------------------------------"
    $jobDetails = Get-Content $sandbox_common/jobConfig.json | ConvertFrom-Json
    $jobId = $jobDetails.jobId

    Write-Host "Spark Pod logs:"
    kubectl logs -l spark-app-name=$jobId `
        -n analytics `
        --all-containers=true `
        --prefix=true `
        --tail=20000 `
        --kubeconfig $kubeConfig
}
else {
    Write-Host "No Spark Application jobConfig.json found."
}
Write-Host "--------------------------------------------------------------"
