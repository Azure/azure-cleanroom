[CmdletBinding()]
param
(
    [string]
    $outDir = "$PSScriptRoot/generated",

    [string]
    $ccfEndpoint = "https://host.docker.internal:9081",

    [string]
    $ccfOutDir = "",

    [string]
    $contractId = "collab1",

    [switch]
    $y,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$registryUrl = "localhost:5001",

    [string]$registryTag = "latest",

    [switch]
    $withSecurityPolicy
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
if ($ccfOutDir -eq "") {
    $ccfOutDir = "$root/test/onebox/multi-party-collab/generated/ccf"
}

$serviceCert = $ccfOutDir + "/service_cert.pem"
if (-not (Test-Path -Path $serviceCert)) {
    throw "serviceCert at $serviceCert does not exist."
}

if ($env:GITHUB_ACTIONS -eq "true" -and $ccfEndpoint -eq "https://host.docker.internal:9081") {
    # 172.17.0.1: https://stackoverflow.com/questions/48546124/what-is-the-linux-equivalent-of-host-docker-internal
    $ccfEndpoint = "https://172.17.0.1:9081"
}

mkdir -p "$outDir/configurations"
$nginxConfig = "$outDir/configurations/nginx-config"

az cleanroom config init --cleanroom-config $nginxConfig

az cleanroom config add-application `
    --cleanroom-config $nginxConfig `
    --name nginx-hello `
    --image "docker.io/nginxdemos/nginx-hello:plain-text@sha256:d976f016b32fc381dfb74119cc421d42787b5a63a6b661ab57891b7caa5ad12e" `
    --cpu 0.5 `
    --memory 4

az cleanroom config add-application-endpoint `
    --cleanroom-config $nginxConfig `
    --application-name nginx-hello `
    --port 8080 `
    --policy cleanroomsamples.azurecr.io/nginx-hello/nginx-hello-policy@sha256:c71b70a70dfad8279d063ab68d80df6d8d407ba8359a68c6edb3e99a25c77575

az cleanroom config view `
    --cleanroom-config $nginxConfig `
    --output-file $outDir/configurations/cleanroom-config

az cleanroom config validate --cleanroom-config $outDir/configurations/cleanroom-config

$data = Get-Content -Raw $outDir/configurations/cleanroom-config
az cleanroom governance contract create `
    --data "$data" `
    --id $contractId `
    --governance-client "ob-nginx-client"

# Submitting a contract proposal.
$version = (az cleanroom governance contract show `
        --id $contractId `
        --query "version" `
        --output tsv `
        --governance-client "ob-nginx-client")

az cleanroom governance contract propose `
    --version $version `
    --id $contractId `
    --governance-client "ob-nginx-client"

$contract = (az cleanroom governance contract show `
        --id $contractId `
        --governance-client "ob-nginx-client" | ConvertFrom-Json)

# Accept it.
az cleanroom governance contract vote `
    --id $contractId `
    --proposal-id $contract.proposalId `
    --action accept `
    --governance-client "ob-nginx-client"


mkdir -p $outDir/deployments
# Set overrides if a non-mcr registry is to be used for clean room container images.
if ($registry -ne "mcr") {
    $env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL = $registryUrl
    $env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL = "${registryUrl}/sidecar-digests:$registryTag"
}

if ($withSecurityPolicy) {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-nginx-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option cached-debug
}
else {
    az cleanroom governance deployment generate `
        --contract-id $contractId `
        --governance-client "ob-nginx-client" `
        --output-dir $outDir/deployments `
        --security-policy-creation-option allow-all
}

az cleanroom governance deployment template propose `
    --template-file $outDir/deployments/cleanroom-arm-template.json `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

az cleanroom governance deployment policy propose `
    --policy-file $outDir/deployments/cleanroom-governance-policy.json `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

az cleanroom governance ca propose-enable `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

$clientName = "ob-nginx-client"
pwsh $PSScriptRoot/../verify-deployment-proposals.ps1 `
    -cleanroomConfig $nginxConfig `
    -governanceClient $clientName

# Vote on the proposed deployment template.
$proposalId = az cleanroom governance deployment template show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the proposed cce policy.
$proposalId = az cleanroom governance deployment policy show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

# Vote on the proposed CA enable.
$proposalId = az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client $clientName `
    --query "proposalIds[0]" `
    --output tsv

az cleanroom governance proposal vote `
    --proposal-id $proposalId `
    --action accept `
    --governance-client $clientName

az cleanroom governance ca generate-key `
    --contract-id $contractId `
    --governance-client "ob-nginx-client"

az cleanroom governance ca show `
    --contract-id $contractId `
    --governance-client "ob-nginx-client" `
    --query "caCert" `
    --output tsv > $outDir/cleanroomca.crt
