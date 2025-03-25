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
  $imageName = "$repo/code-launcher:$tag"
}
else {
  $imageName = "code-launcher:$tag"
}

$root = git rev-parse --show-toplevel
docker image build -t $imageName -f $PSScriptRoot/../docker/Dockerfile.code-launcher $root
if ($push) {
  docker push $imageName
}