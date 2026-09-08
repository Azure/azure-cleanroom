$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

Build-DockerImage `
    -f $PSScriptRoot/Dockerfile.tsp-codegen -t tsp-codegen $PSScriptRoot