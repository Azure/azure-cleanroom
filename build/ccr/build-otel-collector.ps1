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
  $imageName = "$repo/otel-collector:$tag"
}
else {
  $imageName = "otel-collector:$tag"
}

$root = git rev-parse --show-toplevel

docker image build -t $imageName `
  -f $PSScriptRoot/../docker/Dockerfile.otel-collector $root

if ($push) {
  docker push $imageName
}