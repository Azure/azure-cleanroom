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

# Create environment variables dictionary for cluster provider client container
$envVars = @{}
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE"] = "$repo/cleanroom-cluster/cleanroom-cluster-provider-client:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE"] = "$repo/ccr-proxy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE"] = "$repo/otel-collector:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE"] = "$repo/ccr-governance:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE"] = "$repo/ccr-governance-virtual:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE"] = "$repo/skr:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE"] = "$repo/local-skr:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE"] = "$repo/workloads/cleanroom-spark-analytics-agent:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL"] = "$ociEndpoint/workloads/helm/cleanroom-spark-analytics-agent:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/cleanroom-spark-analytics-agent-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE"] = "$repo/workloads/cleanroom-spark-frontend:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL"] = "$ociEndpoint/workloads/helm/cleanroom-spark-frontend:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/cleanroom-spark-frontend-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE"] = "$repo/workloads/kserve-inferencing-agent:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL"] = "$ociEndpoint/workloads/helm/kserve-inferencing-agent:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/kserve-inferencing-agent-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE"] = "$repo/workloads/kserve-inferencing-frontend:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL"] = "$ociEndpoint/workloads/helm/kserve-inferencing-frontend:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/kserve-inferencing-frontend-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL"] = "$ociEndpoint/k8s-node/api-server-proxy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL"] = "$repo"

# Frontend specific
$envVars["AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL"] = "$repo"
$envVars["AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL"] = "$repo/sidecar-digests:$tag"
$envVars["AZCLI_CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL"] = "$repo/cvm-measurements:$tag"
$envVars["AZCLI_CLEANROOM_RUNTIME_DIGESTS_DOCUMENT_URL"] = "$repo/inf-runtime-digests:$tag"

# Analytics App Specific.
$digest = Get-Digest -repo $repo -containerName "workloads/cleanroom-spark-analytics-app" -tag $tag
$envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL"] = "$repo/workloads/cleanroom-spark-analytics-app@$digest"
$envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL"] = "$repo/policies/workloads/cleanroom-spark-analytics-app-security-policy:$tag"

# Write environment variables to file
$envFilePath = "$outDir/cluster-provider.env"
$envFileContent = $envVars.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
$envFileContent | Out-File -FilePath $envFilePath -Encoding utf8

Write-Host "Starting deployment of clean room cluster $clusterName on CACI in RG $resourceGroup."
az cleanroom cluster up `
    --name $clusterName `
    --resource-group $resourceGroup `
    --location $location `
    --workspace-folder $outDir `
    --provider-client $clusterProviderProjectName `
    --env-file $envFilePath

$infraType = "aks"
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