param(
    [switch]
    $localenv,

    [switch]
    $skipInstall,

    [string]$output = "$PSScriptRoot/bin/azext_cleanroom/dist",

    [switch]
    $push,

    [string]
    $repo = "localhost:5000",

    [string]
    $tag = "1.0.0"
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function create_version_file([string]$file_path, [string]$whlTag) {
    $versionContent = "VERSION = `"$whlTag`""
    New-Item -ItemType File -Path $file_path -Force -Value $versionContent
}

$whlTag = $tag
if (!$tag.Contains('.')) {
    # If the input tag is not a valid version, then use a placeholder value.
    # This is to have a valid version in the private / builds/test runs.
    $whlTag = "1.0.42"
}

if (!$localenv) {
    . $PSScriptRoot/helpers.ps1
    create_version_file "$PSScriptRoot/../src/tools/azure-cli-extension/cleanroom/azext_cleanroom/version.py" $whlTag

    # See https://docs.docker.com/build/guide/export/ for --output usage reference.
    docker image build `
        --output=$output --target=dist `
        -f $PSScriptRoot/docker/Dockerfile.azcliext.cleanroom "$PSScriptRoot/.."
    Write-Host "Built $output/cleanroom-$whlTag-py2.py3-none-any.whl"
    if ($push) {
        Push-Location $output
        oras push `
            $repo/cli/cleanroom-whl:$tag `
            ./cleanroom-$whlTag-py2.py3-none-any.whl
        Pop-Location
    }
    elseif ($skipInstall) {
        Write-Host "run 'az extension add -y --source $output/cleanroom-$whlTag-py2.py3-none-any.whl' to install the extension" $whlTag
    }
    else {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        az extension remove --name cleanroom 2>$null
        az extension add -y --source $output/cleanroom-$whlTag-py2.py3-none-any.whl --allow-preview true
    }
}
else {
    # Build and install changes using local whl file.
    # https://github.com/Azure/azure-cli/blob/master/doc/extensions/authoring.md#building

    $root = $(git rev-parse --show-toplevel)
    $extname = "cleanroom"
    create_version_file "$root/src/tools/azure-cli-extension/cleanroom/azext_cleanroom/version.py" $whlTag
    azdev extension build $extname --dist-dir $root/src/tools/azure-cli-extension/cleanroom/dist

    if (!$skipInstall) {
        azdev extension remove $extname
        az extension remove --name $extname
        az extension add -y --source $root/src/tools/azure-cli-extension/cleanroom/dist/cleanroom-$whlTag-py2.py3-none-any.whl --allow-preview true
    }
}
