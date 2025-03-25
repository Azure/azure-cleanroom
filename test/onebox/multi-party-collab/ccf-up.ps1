[CmdletBinding()]
param (
    [string]$resourceGroup,
    [string]$ccfName,
    [string]$location,
    [string]$initialMemberName,
    [string]$memberCertPath,
    [string]$repo,
    [string]$tag,
    [switch]$allowAll,
    [string]$outDir = ""
)

if ($outDir -eq "") {
    $outDir = "$($MyInvocation.PSScriptRoot)/sandbox_common"
}

# set environment variables so that ccf provider client container uses these when it
# gets started via the ccf up command below.
$env:AZCLI_CCF_PROVIDER_CLIENT_IMAGE = "$repo/ccf/ccf-provider-client:$tag"
$env:AZCLI_CCF_PROVIDER_PROXY_IMAGE = "$repo/ccr-proxy:$tag"
$env:AZCLI_CCF_PROVIDER_ATTESTATION_IMAGE = "$repo/ccr-attestation:$tag"
$env:AZCLI_CCF_PROVIDER_SKR_IMAGE = "$repo/skr:$tag"
$env:AZCLI_CCF_PROVIDER_NGINX_IMAGE = "$repo/ccf/ccf-nginx:$tag"
$env:AZCLI_CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE = "$repo/ccf/app/run-js/virtual:$tag"
$env:AZCLI_CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE = "$repo/ccf/app/run-js/snp:$tag"
$env:AZCLI_CCF_PROVIDER_RECOVERY_AGENT_IMAGE = "$repo/ccf/ccf-recovery-agent:$tag"
$env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_IMAGE = "$repo/ccf/ccf-recovery-service:$tag"
$env:AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL = "$repo"
$env:AZCLI_CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL = "$repo/policies/ccf/ccf-network-security-policy:$tag"
$env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL = "$repo/policies/ccf/ccf-recovery-service-security-policy:$tag"

$env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$tag"
$env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$tag"
$env:AZCLI_CGS_JSAPP_IMAGE = "$repo/cgs-js-app:$tag"
$env:AZCLI_CGS_CONSTITUTION_IMAGE = "$repo/cgs-constitution:$tag"

$policyOption = $allowAll ? "allow-all" : "cached-debug"
Write-Host "Starting deployment of CCF $ccfName on CACI in RG $resourceGroup with $policyOption security policy."
az cleanroom ccf network up `
    --name $ccfName `
    --resource-group $resourceGroup `
    --location $location `
    --security-policy-creation-option $policyOption `
    --workspace-folder $outDir

# Below is the gov client name that network up command will start.
$cgsProjectName = $ccfName + "-operator-governance"
$proposal = (az cleanroom governance member add `
        --certificate $memberCertPath `
        --identifier $initialMemberName `
        --governance-client $cgsProjectName | ConvertFrom-Json)
if ($proposal.proposalState -ne "Accepted") {
    throw "Expecting add member proposal to get Accepted as no other active members should exist at this point. Proposal state is {$($proposal.proposalState)}. proposalId: {$($proposal.proposalId)}"
}