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
  $imageName = "$repo/inmemory-keyprovider:$tag"
}
else {
  $imageName = "inmemory-keyprovider:$tag"
}

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"
docker image build -t $imageName `
  -f $buildRoot/docker/samples/aa-flow-based-lending/Dockerfile.inmemory-keyprovider "$buildRoot/.."
if ($push) {
  docker push $imageName
}
