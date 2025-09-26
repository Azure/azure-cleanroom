param(
  [parameter(Mandatory = $false)]
  [string]$tag = "latest",

  [parameter(Mandatory = $false)]
  [string]$repo = "docker.io",

  [parameter(Mandatory = $false)]
  [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1
 
if ($repo) {
  $imageName = "$repo/ccr-attestation:$tag"
}
else {
  $imageName = "ccr-attestation:$tag"
}

$root = git rev-parse --show-toplevel

docker build -t $imageName -f $PSScriptRoot/../docker/Dockerfile.ccr-attestation $root
if ($push) {
  docker push $imageName
}