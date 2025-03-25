[CmdletBinding()]
param
(
    [int]
    $nodeCount = 1,

    [string]
    [ValidateSet("Trace", "Debug", "Info", "Fail", "Fatal")]
    $nodeLogLevel = "Debug",

    [string]
    [ValidateSet("cached", "cached-debug", "allow-all", "user-supplied")]
    $securityPolicyCreationOption = "allow-all",

    [string]
    $securityPolicy = "",

    [switch]
    $OneStepRecovery,

    [switch]
    $confidentialRecovery,

    [string]
    $targetNetworkName = "",

    [string]
    $recoveryServiceName = "",

    [string]
    $recoveryMemberName = "",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$attemptSuffix = (date +"%Y_%m_%d_%I_%M_%p")
$sandbox_common = "$PSScriptRoot/sandbox_common"
$ccf = $(Get-Content $sandbox_common/ccf.json | ConvertFrom-Json)
$ccfEndpoint = $ccf.endpoint
$infraType = $ccf.infraType

$ccfProviderProjectName = "ccf-provider"
$operatorName = "ccf-operator"

$networkToRecover = $ccf.name
$inplaceRecovery = $true
if ($targetNetworkName -ne "" -and $targetNetworkName -ne $networkToRecover) {
    $cgsProjectName = "ccf-provider-governance-$targetNetworkName"
    $networkName = $targetNetworkName
    $inplaceRecovery = $false
}
else {
    # Target network is same as the network being recovered.
    $cgsProjectName = "ccf-provider-governance"
    $networkName = $networkToRecover
}

if ($confidentialRecovery) {
    $recovery = $(Get-Content $sandbox_common/recoveryResources.json | ConvertFrom-Json)

    if ($recoveryMemberName -eq "") {
        $recoveryMemberName = $recovery.confidentialRecovererMemberName
    }

    if ($recoveryServiceName -eq "") {
        $recoveryServiceName = $networkToRecover
    }
}

$securityPolicyBase64 = ""
if (Test-Path $securityPolicy) {
    $securityPolicyBase64 = cat $securityPolicy | base64 -w 0
}

if ($OneStepRecovery) {
    if ($confidentialRecovery) {
        Write-Output "Recovering $networkName network via CCF recovery service $recoveryServiceName as a 1-node network in one step."
        $response = az cleanroom ccf network recover `
            --name $networkToRecover `
            --node-log-level $nodeLogLevel `
            --security-policy-creation-option $securityPolicyCreationOption `
            --security-policy $securityPolicyBase64 `
            --infra-type $infraType `
            --confidential-recovery-service-name $recoveryServiceName `
            --confidential-recovery-member-name $recoveryMemberName `
            --previous-service-cert $sandbox_common/service_cert.pem `
            --provider-config $sandbox_common/providerConfig.json
    }
    else {
        Write-Output "Recovering $networkName network via operator encryption key as a 1-node network in one step."
        if (Test-Path $sandbox_common/${operatorName}_enc_key.id) {
            $response = az cleanroom ccf network recover `
                --name $networkToRecover `
                --node-log-level $nodeLogLevel `
                --security-policy-creation-option $securityPolicyCreationOption `
                --security-policy $securityPolicyBase64 `
                --infra-type $infraType `
                --operator-recovery-encryption-key-id $sandbox_common/${operatorName}_enc_key.id `
                --previous-service-cert $sandbox_common/service_cert.pem `
                --provider-config $sandbox_common/providerConfig.json
        }
        else {
            $response = az cleanroom ccf network recover `
                --name $networkToRecover `
                --node-log-level $nodeLogLevel `
                --security-policy-creation-option $securityPolicyCreationOption `
                --security-policy $securityPolicyBase64 `
                --infra-type $infraType `
                --operator-recovery-encryption-private-key $sandbox_common/${operatorName}_enc_privk.pem `
                --previous-service-cert $sandbox_common/service_cert.pem `
                --provider-config $sandbox_common/providerConfig.json
        }
    }

    $ccfEndpoint = ($response | ConvertFrom-Json).endpoint
    $response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
    # Trimming an extra new-line character added to the cert.
    $serviceCertStr = $response.service_certificate.TrimEnd("`n")
    mv "$sandbox_common/service_cert.pem" "$sandbox_common/service_cert_$attemptSuffix.pem"
    $serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

    Write-Output "Network health:"
    az cleanroom ccf network show-health `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $ccfProviderProjectName

    Write-Output "CCF network recovered:"
    $response = az cleanroom ccf network show `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $ccfProviderProjectName
    Write-Output $response
    Write-Output $response | Out-File $sandbox_common/ccf.json
}
else {
    Write-Output "Deleting any existing network $networkToRecover while retaining storage."
    az cleanroom ccf network delete `
        --name $networkToRecover `
        --infra-type $infraType `
        --delete-option retain-storage `
        --provider-config $sandbox_common/providerConfig.json
    if (!$inplaceRecovery) {
        Write-Output "Deleting any existing recovery network $networkName while removing storage."
        az cleanroom ccf network delete `
            --name $networkName `
            --infra-type $infraType `
            --delete-option delete-storage `
            --provider-config $sandbox_common/providerConfig.json
    }

    Write-Output "Recovering a $nodeCount node public network $networkToRecover as network $networkName."
    $response = az cleanroom ccf network recover-public-network `
        --name $networkToRecover `
        --target-network-name $networkName `
        --node-count $nodeCount `
        --node-log-level $nodeLogLevel `
        --security-policy-creation-option $securityPolicyCreationOption `
        --security-policy $securityPolicyBase64 `
        --infra-type $infraType `
        --previous-service-cert $sandbox_common/service_cert.pem `
        --provider-config $sandbox_common/providerConfig.json

    $ccfEndpoint = ($response | ConvertFrom-Json).endpoint
    $serviceStatus = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json).service_status

    # Open the recovery network as the operator.
    Write-Output "Service status is: $serviceStatus. Opening network $networkName."
    az cleanroom ccf network transition-to-open `
        --name $networkName `
        --infra-type $infraType `
        --previous-service-cert $sandbox_common/service_cert.pem `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $ccfProviderProjectName

    $serviceStatus = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json).service_status

    # Submit the decrypted recovery share.
    $nodeState = (curl "$ccfEndpoint/node/state" -k --silent | ConvertFrom-Json)
    Write-Output "Node state is: $nodeState. Service status is: $serviceStatus."
    if ($confidentialRecovery) {
        Write-Output "Requesting CCF recovery service for submitting recovery share for network $networkName."
        $recSvc = (az cleanroom ccf recovery-service show `
                --name $recoveryServiceName `
                --infra-type $infraType `
                --provider-config $sandbox_common/providerConfig.json | ConvertFrom-Json)
        $agentConfig = @{}
        $agentConfig.recoveryService = @{}
        $agentConfig.recoveryService.endpoint = $recSvc.endpoint
        $agentConfig.recoveryService.serviceCert = $recSvc.serviceCert
        $agentConfig | ConvertTo-Json -Depth 100 > $sandbox_common/submitRecoveryShare-RecoveryAgentConfig.json

        az cleanroom ccf network recovery-agent submit-recovery-share `
            --network-name $networkName `
            --member-name $recoveryMemberName `
            --infra-type $infraType `
            --agent-config $sandbox_common/submitRecoveryShare-RecoveryAgentConfig.json `
            --provider-config $sandbox_common/providerConfig.json
    }
    else {
        Write-Output "Submitting operator recovery share for network $networkName."
        if (Test-Path $sandbox_common/${operatorName}_enc_key.id) {
            az cleanroom ccf network submit-recovery-share `
                --name $networkName `
                --infra-type $infraType `
                --encryption-key-id $sandbox_common/${operatorName}_enc_key.id `
                --provider-config $sandbox_common/providerConfig.json `
                --provider-client $ccfProviderProjectName
        }
        else {
            az cleanroom ccf network submit-recovery-share `
                --name $networkName `
                --infra-type $infraType `
                --encryption-private-key $sandbox_common/${operatorName}_enc_privk.pem `
                --provider-config $sandbox_common/providerConfig.json `
                --provider-client $ccfProviderProjectName
        }
    }

    $response = (curl "$ccfEndpoint/node/network" -k --silent | ConvertFrom-Json)
    # Trimming an extra new-line character added to the cert.
    $serviceCertStr = $response.service_certificate.TrimEnd("`n")
    mv "$sandbox_common/service_cert.pem" "$sandbox_common/service_cert_$attemptSuffix.pem"
    $serviceCertStr | Out-File "$sandbox_common/service_cert.pem"

    Write-Output "Network health:"
    az cleanroom ccf network show-health `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $ccfProviderProjectName

    Write-Output "CCF network recovered:"
    $response = az cleanroom ccf network show `
        --name $networkName `
        --infra-type $infraType `
        --provider-config $sandbox_common/providerConfig.json `
        --provider-client $ccfProviderProjectName
    Write-Output $response
    mv "$sandbox_common/ccf.json" "$sandbox_common/ccf_$attemptSuffix.json"
    Write-Output $response | Out-File $sandbox_common/ccf.json
}

# Deploy the governance client for the operator to take any gov actions.
if ($repo -ne "") {
    $server = $repo
    $localTag = $tag
    $env:AZCLI_CGS_CLIENT_IMAGE = "$server/cgs-client:$localTag"
    $env:AZCLI_CGS_UI_IMAGE = "$server/cgs-ui:$localTag"
}
else {
    $env:AZCLI_CGS_CLIENT_IMAGE = ""
    $env:AZCLI_CGS_UI_IMAGE = ""
}

if (Test-Path $sandbox_common/${operatorName}_cert.id) {
    az cleanroom governance client deploy `
        --ccf-endpoint $ccfEndpoint `
        --signing-cert-id $sandbox_common/${operatorName}_cert.id `
        --service-cert $sandbox_common/service_cert.pem `
        --name $cgsProjectName
}
else {
    az cleanroom governance client deploy `
        --ccf-endpoint $ccfEndpoint `
        --signing-key $sandbox_common/${operatorName}_privk.pem `
        --signing-cert $sandbox_common/${operatorName}_cert.pem `
        --service-cert $sandbox_common/service_cert.pem `
        --name $cgsProjectName
}
