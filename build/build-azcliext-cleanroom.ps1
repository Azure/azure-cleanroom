param(
    [switch]
    $localenv,

    [switch]
    $skipInstall,

    [string]$output = "$PSScriptRoot/bin/azext_cleanroom/dist"
)

$version = "1.0.0"

if (!$localenv) {
    . $PSScriptRoot/helpers.ps1

    # See https://docs.docker.com/build/guide/export/ for --output usage reference.
    docker image build `
        --output=$output --target=dist `
        -f $PSScriptRoot/docker/Dockerfile.azcliext.cleanroom "$PSScriptRoot/.."
    CheckLastExitCode
    if ($skipInstall) {
        Write-Host "run 'az extension add -y --source $output/cleanroom-$version-py2.py3-none-any.whl' to install the extension"
    }
    else {
        az extension remove --name cleanroom 2>$null
        az extension add -y --source $output/cleanroom-$version-py2.py3-none-any.whl --allow-preview true
    }
}
else {
    # Build and install changes using local whl file.
    # https://github.com/Azure/azure-cli/blob/master/doc/extensions/authoring.md#building

    $root = $(git rev-parse --show-toplevel)
    $extname = "cleanroom"
    azdev extension build $extname --dist-dir $root/src/tools/azure-cli-extension/cleanroom/dist

    if (!$skipInstall) {
        azdev extension remove $extname
        az extension remove --name $extname
        az extension add -y --source $root/src/tools/azure-cli-extension/cleanroom/dist/cleanroom-$version-py2.py3-none-any.whl --allow-preview true
    }
}
