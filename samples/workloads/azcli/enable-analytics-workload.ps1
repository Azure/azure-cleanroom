[CmdletBinding()]
param
(
    [string]
    [ValidateSet("cached", "cached-debug", "allow-all")]
    $securityPolicyCreationOption = "allow-all",

    [string]
    $outDir = "",

    [string]
    $configEndpointFile = "",

    # Number of VN2 workload-pool nodes. Defaults to the CLEANROOM_ANALYTICS_POOL_NODE_COUNT
    # env var when set (the TPC-DS stress test sets 3); otherwise 2 (e.g. big-data e2e).
    [int]
    $analyticsWorkloadPoolNodeCount = ($env:CLEANROOM_ANALYTICS_POOL_NODE_COUNT ?? 2)
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1
Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
}
else {
    $sandbox_common = $outDir
}

$clCluster = Get-Content $sandbox_common/cl-cluster.json | ConvertFrom-Json
$clusterName = $clCluster.name
$infraType = $clCluster.infraType
$repoConfig = Get-Content $sandbox_common/repoConfig.json | ConvertFrom-Json
$repo = $repoConfig.repo
$tag = $repoConfig.tag
$clusterProviderProjectName = $repoConfig.clusterProviderProjectName
$kubeConfig = "${sandbox_common}/k8s-credentials.yaml"

if ($configEndpointFile -eq "") {
    pwsh $PSScriptRoot/deploy-analytics-workload-config-endpoint.ps1 `
        -outDir $sandbox_common `
        -repo $repo `
        -tag $tag
    $configEndpointFile = "${sandbox_common}/analytics-workload-config-endpoint.json"
}

$configUrl = (Get-Content $configEndpointFile | ConvertFrom-Json).url
$configUrlCaCert = (Get-Content $configEndpointFile | ConvertFrom-Json).caCert
Write-Output "Enabling analytics workload on cluster '$clusterName'."
az cleanroom cluster update `
    --name $clusterName `
    --infra-type $infraType `
    --enable-analytics-workload `
    --analytics-workload-config-url $configUrl `
    --analytics-workload-config-url-ca-cert $configUrlCaCert `
    --analytics-workload-security-policy-creation-option $securityPolicyCreationOption `
    --analytics-workload-pool-node-count $analyticsWorkloadPoolNodeCount `
    --provider-config $sandbox_common/providerConfig.json `
    --provider-client $clusterProviderProjectName

$timeout = New-TimeSpan -Minutes 10
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ($true) {
    $analyticsEndpoint = az cleanroom cluster show `
        --name $clusterName `
        --infra-type $infraType `
        --query "analyticsWorkloadProfile.endpoint" `
        --output tsv `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $clusterProviderProjectName
    if (![string]::IsNullOrEmpty($analyticsEndpoint)) {
        Write-Output "Analytics endpoint is up at: $analyticsEndpoint"
        break
    }

    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for analytics endpoint to become available."
    }

    Write-Output "Waiting for analytics endpoint to be up..."
    Start-Sleep -Seconds 5
}

# Use kubectl proxy on :8181 to reach the in-cluster analytics service /ready endpoint.
# Reap stale in-session job records for a kubectl proxy this script started earlier.
Get-Job -Command "*kubectl*8181*" | Stop-Job -ErrorAction SilentlyContinue
Get-Job -Command "*kubectl*8181*" | Remove-Job -ErrorAction SilentlyContinue

# If :8181 is held by a process this script did not start, fail and name the owner.
# We deliberately do not kill it; the user decides whether it should be stopped.
& {
    $PSNativeCommandUseErrorActionPreference = $false
    $owner = if ($IsWindows) {
        $conn = Get-NetTCPConnection -LocalPort 8181 -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($conn) {
            $proc = Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue
            if ($proc) { "pid=$($proc.Id) name=$($proc.ProcessName)" } else { "pid=$($conn.OwningProcess)" }
        }
    }
    else {
        & sh -c "ss -lptnH 'sport = :8181' 2>/dev/null | head -1" 2>$null
    }
    if ($owner) {
        Write-Host "Port 8181 is held by: $owner"
        throw "Port 8181 is in use. Free it (or stop the conflicting process) and re-run."
    }
}

kubectl proxy --port 8181 --kubeconfig $kubeConfig &
$serviceAddress = "http://localhost:8181/api/v1/namespaces/cleanroom-spark-analytics-agent/services/https:cleanroom-spark-analytics-agent:443/proxy"

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    while ((curl -o /dev/null -w "%{http_code}" -k -s ${serviceAddress}/ready) -ne "200") {
        Write-Output "Waiting for analytics endpoint to be ready at ${serviceAddress}/ready"
        Start-Sleep -Seconds 3
        if ($stopwatch.elapsed -gt $timeout) {
            # Re-run the command once to log its output.
            curl -k -s ${serviceAddress}/ready
            throw "Hit timeout waiting for analytics endpoint to be ready."
        }
    }
}

$response = az cleanroom cluster show `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $sandbox_common/providerConfig.json `
    --provider-client $clusterProviderProjectName
$response | Out-File $sandbox_common/cl-cluster.json

Write-Output "Analytics workload is enabled."
