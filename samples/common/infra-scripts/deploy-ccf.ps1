function Deploy-Ccf {
    [CmdletBinding()]
    param (
        [string]$resourceGroup,
        [string]$ccfName,
        [string]$location,
        [string]$initialMemberName,
        [string]$memberCertPath,
        [string]$registryUrl,
        [string]$registryTag,
        [switch]$allowAll,
        [string]$outDir = ""
    )

    if ($outDir -eq "") {
        $outDir = "$($MyInvocation.PSScriptRoot)/sandbox_common"
    }

    # set environment variables so that ccf provider client container uses these when it
    # gets started via the ccf up command below.
    $env:AZCLI_CCF_PROVIDER_CLIENT_IMAGE = "$registryUrl/ccf/ccf-provider-client:$registryTag"
    $env:AZCLI_CCF_PROVIDER_PROXY_IMAGE = "$registryUrl/ccr-proxy:$registryTag"
    $env:AZCLI_CCF_PROVIDER_ATTESTATION_IMAGE = "$registryUrl/ccr-attestation:$registryTag"
    $env:AZCLI_CCF_PROVIDER_SKR_IMAGE = "$registryUrl/skr:$registryTag"
    $env:AZCLI_CCF_PROVIDER_NGINX_IMAGE = "$registryUrl/ccf/ccf-nginx:$registryTag"
    $env:AZCLI_CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE = "$registryUrl/ccf/app/run-js/virtual:$registryTag"
    $env:AZCLI_CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE = "$registryUrl/ccf/app/run-js/snp:$registryTag"
    $env:AZCLI_CCF_PROVIDER_RECOVERY_AGENT_IMAGE = "$registryUrl/ccf/ccf-recovery-agent:$registryTag"
    $env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_IMAGE = "$registryUrl/ccf/ccf-recovery-service:$registryTag"
    $env:AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL = "$registryUrl"
    $env:AZCLI_CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL = "$registryUrl/policies/ccf/ccf-network-security-policy:$registryTag"
    $env:AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL = "$registryUrl/policies/ccf/ccf-recovery-service-security-policy:$registryTag"

    $env:AZCLI_CGS_CLIENT_IMAGE = "$registryUrl/cgs-client:$registryTag"
    $env:AZCLI_CGS_UI_IMAGE = "$registryUrl/cgs-ui:$registryTag"
    $env:AZCLI_CGS_JSAPP_IMAGE = "$registryUrl/cgs-js-app:$registryTag"
    $env:AZCLI_CGS_CONSTITUTION_IMAGE = "$registryUrl/cgs-constitution:$registryTag"

    $policyOption = $allowAll ? "allow-all" : "cached"
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
}