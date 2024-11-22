param(
  [parameter(Mandatory=$false)]
  [string]$tag = "latest",

  [parameter(Mandatory=$false)]
  [string]$repo = "docker.io",

  [parameter(Mandatory=$false)]
  [switch]$push
)

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$external = Join-Path $root -ChildPath "/external"
# git submodule update --init --recursive $external/azure-storage-fuse

# See https://docs.docker.com/build/guide/export/ for --output usage reference.
docker image build `
  --output=$PSScriptRoot/bin --target=binaries `
  -f $PSScriptRoot/../docker/Dockerfile.blobfuse2 `
  $root
CheckLastExitCode

if ($repo)
{
  $imageName = "$repo/blobfuse2:$tag"
}
else
{
  $imageName = "blobfuse2:$tag"
}

# Build blobfuse2 container image using the blobfuse2 binary built above.
Write-Host "Build container for blobfuse2"
docker image build -t $imageName -f "$root/external/azure-storage-fuse/docker/Dockerfile" "$PSScriptRoot/bin"
CheckLastExitCode

if ($push)
{
  docker push $imageName
  CheckLastExitCode
}
