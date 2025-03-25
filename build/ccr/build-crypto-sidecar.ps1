param(
  [int]$port = 8283,

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
  $imageName = "$repo/crypto-sidecar:$tag"
}
else {
  $imageName = "crypto-sidecar:$tag"
}

$root = git rev-parse --show-toplevel
$external = Join-Path $root -ChildPath "/external"
git submodule update --init --recursive $external/rahasya

$buildRoot = "$root/build"
docker image build -t $imageName `
  -f $buildRoot/docker/samples/aa-flow-based-lending/Dockerfile.crypto "$buildRoot/../external/rahasya" `
  --build-arg PORT=$port
if ($push) {
  docker push $imageName
}
