param(
    [string]
    $rg = "gsinha-test-rg",

    [string]
    $acrLoginServer = "docker.io/gausinha",

    [string]
    $ServiceDnsName = "",

    [switch]
    $BuildAndPush
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

# pwsh $PSScriptRoot/remove-ccf.ps1

. $root/build/helpers.ps1

if ($BuildAndPush) {
    if ($ServiceDnsName -eq "") {
        throw "Error: -ServiceDnsName must be specified"
    }

    # Create the CCF configuration with the supplied $serviceDnsName.
    jq --arg service_dns_name $ServiceDnsName `
        '.network.acme.configurations."my-acme-cfg".service_dns_name = $service_dns_name' `
        $root/src/governance/ccf-app/js/config/cchost_config_virtual_js-acme.json.template `
        > $root/src/governance/ccf-app/js/config/cchost_config_virtual_js-acme.json

    Write-Output "Building ccf image with acme configuration"
    pwsh $PSScriptRoot/build-ccf.ps1
    Write-Output "Pushing the ccf image to $acrLoginServer"
    pwsh $PSScriptRoot/push-ccf.ps1 -acme
}

Write-Output "Deploying image to ACI"
az deployment group create --resource-group $rg --template-file $PSScriptRoot/aci-ccf-acme.bicep --parameters image="$acrLoginServer/ccf-acme:latest"