param(
    [string]$output = "$PSScriptRoot/bin/governance/ccf-app/js/js/dist"
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/helpers.ps1

# See https://docs.docker.com/build/guide/export/ for --output usage reference.
docker image build `
    --output=$output --target=dist `
    -f $PSScriptRoot/docker/Dockerfile.governance.ccf-app "$PSScriptRoot/../src/governance/ccf-app/js"
