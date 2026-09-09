[CmdletBinding()]
param
(
    [Parameter(Mandatory = $true)]
    [string]$repo,

    [string]$tag = "",

    [string]$outDir = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

. $root/build/helpers.ps1

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
}
else {
    $sandbox_common = $outDir
}

mkdir -p $sandbox_common

# Resolve tag from .last-tag if not explicitly provided.
if ($tag -eq "") {
    $lastTagFile = "$sandbox_common/.last-tag"
    if (Test-Path $lastTagFile) {
        $tag = (Get-Content $lastTagFile -Raw).Trim()
        Write-Host "Using tag from ${lastTagFile}: $tag"
    }
    else {
        throw "No -tag specified and $lastTagFile not found. " +
        "Run build-dev-up-containers.ps1 first or pass -tag explicitly."
    }
}

# ---------------------------------------------------------------------------
# Compute registry endpoints
# ---------------------------------------------------------------------------
$ociEndpoint = $repo
if ($repo.StartsWith("localhost:5000")) {
    if ($env:CODESPACES -ne "true" -and `
            $env:GITHUB_ACTIONS -ne "true") {
        $ociEndpoint = "host.docker.internal:5000"
    }
    elseif ($env:CODESPACES -eq "true") {
        $ociEndpoint = "ccr-registry:5000"
    }
    else {
        $ociEndpoint = "172.17.0.1:5000"
    }
}

if ($repo -eq "localhost:5000") {
    $podReachableRepo = "ccr-registry:5000"
}
else {
    $podReachableRepo = $repo
}

$semanticVersion = Get-SemanticVersionFromTag $tag

# ---------------------------------------------------------------------------
# Cluster provider variables
# ---------------------------------------------------------------------------
$envVars = [ordered]@{}

$envVars["AZCLI_CLEANROOM_OPERATOR_IMAGE"] = `
    "$repo/cleanroom-operator:$tag"
$envVars["AZCLI_CLEANROOM_KARPENTER_PROVIDER_IMAGE"] = `
    "$repo/karpenter-provider-accr:$tag"
$envVars["AZCLI_CLEANROOM_KARPENTER_PROVIDER_CHART_URL"] = `
    "$ociEndpoint/helm/karpenter-provider-accr:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE"] = `
    "$repo/cleanroom-cluster/cleanroom-cluster-provider-client:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE"] = `
    "$repo/ccr-proxy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE"] = `
    "$repo/otel-collector:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE"] = `
    "$repo/local-skr:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE"] = `
    "$repo/skr:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE"] = `
    "$repo/ccr-governance-virtual:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE"] = `
    "$repo/ccr-governance:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE"] = `
    "$repo/workloads/cleanroom-spark-analytics-agent:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL"] = `
    "$ociEndpoint/workloads/helm/cleanroom-spark-analytics-agent:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = `
    "$repo/policies/workloads/cleanroom-spark-analytics-agent-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE"] = `
    "$repo/workloads/cleanroom-spark-frontend:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL"] = `
    "$ociEndpoint/workloads/helm/cleanroom-spark-frontend:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = `
    "$repo/policies/workloads/cleanroom-spark-frontend-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_SPARK_FRONTEND_VERSIONS_DOCUMENT_URL"] = `
    "$repo/versions/workloads/cleanroom-spark-frontend:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE"] = `
    "$repo/workloads/kserve-inferencing-agent:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL"] = `
    "$ociEndpoint/workloads/helm/kserve-inferencing-agent:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL"] = `
    "$repo/policies/workloads/cleanroom-kserve-inferencing-agent-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_OHTTP_GATEWAY_IMAGE"] = `
    "$repo/workloads/ohttp-gateway:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE"] = `
    "$repo/workloads/kserve-inferencing-frontend:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL"] = `
    "$ociEndpoint/workloads/helm/kserve-inferencing-frontend:$semanticVersion"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL"] = `
    "$repo/policies/workloads/cleanroom-kserve-inferencing-frontend-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL"] = `
    "$ociEndpoint/k8s-node/api-server-proxy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_KUBELET_PROXY_PACKAGE_URL"] = `
    "$ociEndpoint/k8s-node/kubelet-proxy:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLEANROOM_BOOT_PACKAGE_URL"] = `
    "$ociEndpoint/k8s-node/cleanroom-boot:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_FLEX_NODE_IMAGE_DIGESTS_URL"] = `
    "$ociEndpoint/cleanroom-image-digests:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL"] = `
    "$repo"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_TAG"] = "$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP"] = `
($repo -eq "localhost:5000" ? "true" : "false")
$envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL"] = `
    "$repo/workloads/cleanroom-spark-analytics-app:$tag"
$envVars["AZCLI_CLEANROOM_CLUSTER_PROVIDER_HOST_SHARED_DIR"] = "/tmp/cleanroom-shared"

$envVars["AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL"] = `
    "$podReachableRepo"
$envVars["AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL"] = `
    "$podReachableRepo/policies/workloads/cleanroom-spark-analytics-app-security-policy:$tag"
$envVars["AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL"] = `
    "$podReachableRepo/sidecar-digests:$tag"
$envVars["AZCLI_CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL"] = `
    "$podReachableRepo/cvm-measurements:$tag"
$envVars["AZCLI_CLEANROOM_INFERENCING_DIGESTS_DOCUMENT_URL"] = `
    "$podReachableRepo/inferencing-digests:$tag"

# ---------------------------------------------------------------------------
# CCF provider variables
# ---------------------------------------------------------------------------
$ccfEnvVars = [ordered]@{}
$ccfEnvVars["AZCLI_CCF_PROVIDER_CLIENT_IMAGE"] = `
    "$repo/ccf/ccf-provider-client:$tag"
$ccfEnvVars["AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL"] = "$repo"
$ccfEnvVars["AZCLI_CCF_PROVIDER_RELEASE_METADATA_CHART_URL"] = `
    "oci://$ociEndpoint/release-metadata"
$ccfEnvVars["AZCLI_CCF_PROVIDER_RELEASE_VERSION"] = $semanticVersion

# ---------------------------------------------------------------------------
# Governance client variables (CGS client/UI image overrides)
# ---------------------------------------------------------------------------
$cgsEnvVars = [ordered]@{}
$cgsEnvVars["AZCLI_CGS_CLIENT_IMAGE"] = "$repo/cgs-client:$tag"
$cgsEnvVars["AZCLI_CGS_UI_IMAGE"] = "$repo/cgs-ui:$tag"
$cgsEnvVars["AZCLI_CGS_CONSTITUTION_IMAGE"] = "$podReachableRepo/cgs-constitution:$tag"
$cgsEnvVars["AZCLI_CGS_JS_APP_IMAGE"] = "$podReachableRepo/cgs-js-app:$tag"

# ---------------------------------------------------------------------------
# Write single merged env file with section comments
# ---------------------------------------------------------------------------
$envFilePath = "$sandbox_common/dev-up.env"
$sections = @(
    @{ Header = "cluster-provider"; Vars = $envVars },
    @{ Header = "ccf-provider"; Vars = $ccfEnvVars },
    @{ Header = "governance-client"; Vars = $cgsEnvVars }
)
$lines = [System.Collections.Generic.List[string]]::new()
foreach ($section in $sections) {
    $lines.Add("# [$($section.Header)]")
    foreach ($entry in $section.Vars.GetEnumerator()) {
        $lines.Add("$($entry.Key)=$($entry.Value)")
    }
    $lines.Add("")
}
$lines | Out-File -FilePath $envFilePath -Encoding utf8

Write-Host -ForegroundColor Green `
    "Env file written to:"
Write-Host -ForegroundColor Green "  $envFilePath"
