[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [switch]
    $NoTest,

    [switch]
    $triggerSnapshotOnCompletion,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = ""
)
#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$sandbox_common = "$PSScriptRoot/sandbox_common"
$ccf = $(Get-Content $sandbox_common/ccf.json | ConvertFrom-Json)
$ccfEndpoint = $ccf.endpoint
Write-Output "Using CCF endpoint: $ccfEndpoint"
pwsh $root/samples/governance/azcli/deploy-cgs.ps1 `
    -ccfEndpoint $ccfEndpoint `
    -outDir $sandbox_common `
    -NoBuild:$NoBuild `
    -NoTest:$NoTest `
    -projectName "member0-governance" `
    -initialMemberName "member0" `
    -registry $registry `
    -repo $repo `
    -tag $tag

if ($triggerSnapshotOnCompletion) {
    Write-Output "Triggering a snapshot post CGS deployment."
    az cleanroom ccf network trigger-snapshot `
        --name $ccf.name `
        --infra-type $ccf.infraType `
        --provider-config $sandbox_common/providerConfig.json
}

$setup = Get-Content $sandbox_common/setup.json | ConvertFrom-Json
$useServiceCertDiscovery = $setup.useServiceCertDiscovery
$operatorName = "ccf-operator"
if (-not (Test-Path $sandbox_common/${operatorName}_cert.id) -and $useServiceCertDiscovery -and !$NoTest) {
    $cgsProjectName = "ccf-provider-governance"
    Write-Output "As use service cert discovery is enabled restarting $cgsProjectName cgs client as constitution/jsapp digest changes when deploy-cgs runs its tests."
    $discoverySettings = (az cleanroom governance client show --name $cgsProjectName | ConvertFrom-Json).serviceCertDiscovery
    $versions = (az cleanroom governance service version --governance-client $cgsProjectName) | ConvertFrom-Json

    Write-Output "Restarting $cgsProjectName with constitution digest: $($versions.constitution.digest), jsapp bundle digest: $($versions.jsapp.digest)"
    if ($repo -ne "") {
        $env:AZCLI_CGS_CLIENT_IMAGE = "$repo/cgs-client:$tag"
        $env:AZCLI_CGS_UI_IMAGE = "$repo/cgs-ui:$tag"
    }
    else {
        $env:AZCLI_CGS_CLIENT_IMAGE = ""
        $env:AZCLI_CGS_UI_IMAGE = ""
    }

    az cleanroom governance client deploy `
        --ccf-endpoint $ccfEndpoint `
        --signing-key $sandbox_common/${operatorName}_privk.pem `
        --signing-cert $sandbox_common/${operatorName}_cert.pem `
        --service-cert-discovery-endpoint $discoverySettings.certificateDiscoveryEndpoint `
        --service-cert-discovery-snp-host-data $discoverySettings.hostData[0] `
        --service-cert-discovery-constitution-digest $versions.constitution.digest `
        --service-cert-discovery-jsapp-bundle-digest $versions.jsapp.digest `
        --name $cgsProjectName
}