param(
  [parameter(Mandatory = $false)]
  [string]$tag = "latest",

  [parameter(Mandatory = $false)]
  [string]$repo = "localhost:5000",

  [parameter(Mandatory = $false)]
  [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

if ($repo) {
  $imageName = "$repo/ccr-governance-virtual:$tag"
}
else {
  $imageName = "ccr-governance-virtual:$tag"
}

$root = git rev-parse --show-toplevel
docker image build -t $imageName --target virtual -f $PSScriptRoot/../docker/Dockerfile.ccr-governance $root

if ($push) {
  docker push $imageName
}