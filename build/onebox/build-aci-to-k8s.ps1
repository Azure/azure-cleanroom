param(
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"
. $buildRoot/helpers.ps1
Build-DockerImage `
    -t "aci-to-k8s" -f $buildRoot/docker/Dockerfile.aci-to-k8s `
    "$buildRoot/../src/tools/aci-to-k8s"