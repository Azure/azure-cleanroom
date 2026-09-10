[CmdletBinding()]
param (
    [string]$resourceGroup,
    [string]$ccfName,
    [string]$location,
    [string]$initialMemberName,
    [string]$memberCertPath,
    [string]$repo,
    [string]$tag,
    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",
    [switch]$allowAll,
    [string]$ccfProviderProjectName = "ccf-provider",
    [string]$outDir = ""
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

if ($outDir -eq "") {
    $outDir = "$($MyInvocation.PSScriptRoot)/sandbox_common"
}

# set environment variables so that ccf provider client container uses these when it
# gets started via the ccf up command below.
$env:AZCLI_CCF_PROVIDER_CLIENT_IMAGE = "$repo/ccf/ccf-provider-client:$tag"
$env:AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL = "$repo"

if ($registry -eq "mcr") {
    # Catalog URL and version are a pair: use neither override for published MCR artifacts.
    $env:AZCLI_CCF_PROVIDER_RELEASE_METADATA_CHART_URL = ""
    $env:AZCLI_CCF_PROVIDER_RELEASE_VERSION = ""
}
else {
    # The release-metadata catalog is resolved by the ccf-provider-client CONTAINER via
    # 'helm show values oci://...', so it must use a container-reachable endpoint, not the
    # host-side localhost:5000 (which resolves to the container itself).
    $ociEndpoint = $repo
    if ($repo.StartsWith("localhost:5000")) {
        if ($env:CODESPACES -ne "true" -and $env:GITHUB_ACTIONS -ne "true") {
            $ociEndpoint = "host.docker.internal:5000"
        }
        elseif ($env:CODESPACES -eq "true") {
            $ociEndpoint = "ccr-registry:5000"
        }
        else {
            # 172.17.0.1 is the Linux equivalent of host.docker.internal.
            $ociEndpoint = "172.17.0.1:5000"
        }
    }
    $env:AZCLI_CCF_PROVIDER_RELEASE_METADATA_CHART_URL =
        "oci://$ociEndpoint/release-metadata"

    # The catalog is a Helm chart; its version derives from the image tag via the same helper the
    # publisher uses (Get-SemanticVersionFromTag), so consumer and producer agree.
    $catalogVersion = Get-SemanticVersionFromTag $tag
    $env:AZCLI_CCF_PROVIDER_RELEASE_VERSION = $catalogVersion
}

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
    --workspace-folder $outDir `
    --provider-client $ccfProviderProjectName

$agentEndpoint = az cleanroom ccf network recovery-agent show `
    --name $ccfName `
    --provider-config $outDir/providerConfig.json `
    --provider-client $ccfProviderProjectName `
    --query endpoint `
    --output tsv
$snpHostData = az cleanroom ccf network show-report `
    --name $ccfName `
    --provider-config $outDir/providerConfig.json `
    --provider-client $ccfProviderProjectName `
    --query reports[0].hostData `
    --output tsv
@"
{
  "endpoint": "$agentEndpoint",
  "snpHostData": "$snpHostData"
}
"@ | Out-File $outDir/ccf.recovery-agent.json

# Below is the gov client name that network up command will start.
$cgsProjectName = $ccfName + "-operator-governance"
$proposal = (az cleanroom governance member add `
        --certificate $memberCertPath `
        --identifier $initialMemberName `
        --governance-client $cgsProjectName | ConvertFrom-Json)
if ($proposal.proposalState -ne "Accepted") {
    # On re-runs active members could already exist.
    # Write-Output "Expecting add member proposal to get Accepted as no other active members should exist at this point. Proposal state is {$($proposal.proposalState)}. proposalId: {$($proposal.proposalId)}"
}