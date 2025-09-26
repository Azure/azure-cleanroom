[CmdletBinding()]
param (
    [string]$resourceGroup,
    [string]$clusterName,
    [string]$location,
    [string]$repo,
    [string]$tag,
    [string]$clusterProviderProjectName = "ob-cleanroom-cluster-provider",
    [string]$outDir = ""
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($outDir -eq "") {
    $outDir = "$($MyInvocation.PSScriptRoot)/sandbox_common"
}

. $PSScriptRoot/helpers.ps1

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

$ociEndpoint = $repo
if ($repo.StartsWith("localhost:5000")) {
    if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
        $ociEndpoint = "host.docker.internal:5000"
    }
    else {
        # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
        $ociEndpoint = "172.17.0.1:5000"
    }
}
$semanticVersion = Get-SemanticVersionFromTag $tag

# set environment variables so that cluster provider client container uses these when it
# gets started via the ccf up command below.
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE = "$repo/cleanroom-cluster/cleanroom-cluster-provider-client:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE = "$repo/ccr-proxy:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_ATTESTATION_IMAGE = "$repo/ccr-attestation:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE = "$repo/otel-collector:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE = "$repo/ccr-governance:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE = "$repo/skr:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE = "$repo/workloads/cleanroom-spark-analytics-agent:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL = "$ociEndpoint/workloads/helm/cleanroom-spark-analytics-agent:$semanticVersion"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL = "$repo/policies/workloads/cleanroom-spark-analytics-agent-security-policy:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE = "$repo/workloads/cleanroom-spark-frontend:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL = "$ociEndpoint/workloads/helm/cleanroom-spark-frontend:$semanticVersion"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL = "$repo/policies/workloads/cleanroom-spark-frontend-security-policy:$tag"
$env:AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL = "$repo"

# Frontend specific
$env:AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL = "$repo"
$env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "$repo/sidecar-digests:$tag"

# Analytics App Specific.
$digest = Get-Digest -repo $repo -containerName "workloads/cleanroom-spark-analytics-app" -tag $tag
$env:AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL = "$repo/workloads/cleanroom-spark-analytics-app@$digest"
$env:AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL = "$repo/policies/workloads/cleanroom-spark-analytics-app-security-policy:$tag"

Write-Host "Starting deployment of clean room cluster $clusterName on CACI in RG $resourceGroup."
az cleanroom cluster up `
    --name $clusterName `
    --resource-group $resourceGroup `
    --location $location `
    --workspace-folder $outDir `
    --provider-client $clusterProviderProjectName

$infraType = "caci"
$response = az cleanroom cluster show `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $outDir/providerConfig.json `
    --provider-client $clusterProviderProjectName
$response | Out-File $outDir/cl-cluster.json

Write-Host "Getting k8s credentials..."
$kubeConfig = "${outDir}/k8s-credentials.yaml"
az cleanroom cluster get-kubeconfig `
    --name $clusterName `
    --infra-type $infraType `
    --provider-config $outDir/providerConfig.json `
    -f $kubeConfig `
    --provider-client $clusterProviderProjectName

@"
{
  "repo": "$repo",
  "tag": "$tag",
  "clusterProviderProjectName": "$clusterProviderProjectName"
}
"@ > $outDir/repoConfig.json