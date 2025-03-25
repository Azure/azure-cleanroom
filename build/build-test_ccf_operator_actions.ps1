param(
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/helpers.ps1

docker image build -t test_ccf_operator_actions -f $PSScriptRoot/docker/Dockerfile.test_ccf_operator_actions "$PSScriptRoot/.."
