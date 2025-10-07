param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [switch]$withRegoPolicy,

    [parameter(Mandatory = $false)]
    [switch]$skipPush,

    [parameter(Mandatory = $false)]
    [string]$digestFileDir = "",

    [parameter(Mandatory = $false)]
    [string[]]
    $containers
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

$ccrContainers = @(
    "blobfuse-launcher",
    "s3fs-launcher",
    "ccr-attestation",
    "ccr-governance",
    "ccr-governance-virtual",
    "ccr-init",
    "ccr-secrets",
    "ccr-proxy",
    "ccr-proxy-ext-processor",
    "ccr-client-proxy",
    "code-launcher",
    "identity",
    "otel-collector",
    "local-skr",
    "skr"
)

$ccrArtefacts = @(
    "ccr-governance-opa-policy"
)

if ($digestFileDir -eq "") {
    $digestFileDir = [IO.Path]::GetTempPath()
}

$push = $skipPush -eq $false
$skipRegoPolicy = $withRegoPolicy -eq $false

if ($push -and $repo -eq "localhost:5000") {
    # Create registry container unless it already exists.
    $reg_name = "ccr-registry"
    $reg_port = "5000"
    $registryImage = "registry:2.7"
    if ($env:GITHUB_ACTIONS -eq "true") {
        $registryImage = "cleanroombuild.azurecr.io/registry:2.7"
    }

    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        $registryState = docker inspect -f '{{.State.Running}}' "${reg_name}" 2>$null
        if ($registryState -ne "true") {
            docker run -d --restart=always -p "127.0.0.1:${reg_port}:5000" --network bridge --name "${reg_name}" $registryImage
        }
    }
}

Write-Host -ForegroundColor DarkGreen "Running $($MyInvocation.MyCommand.Name)..."

$index = 0
$anyCcrArtifactsBuilt = $false
foreach ($container in $ccrContainers) {
    $index++
    if ($null -eq $containers -or $containers.Contains($container)) {
        Write-Host -ForegroundColor DarkGreen "Building $container container ($index/$($ccrContainers.Count))"
        pwsh $buildRoot/ccr/build-$container.ps1 -tag $tag -repo $repo -push:$push
        $anyCcrArtifactsBuilt = $true
    }
    else {
        Write-Host -ForegroundColor DarkBlue "Skipping building $container container ($index/$($ccrContainers.Count))"
    }

    if ($env:GITHUB_ACTIONS -ne "true") {
        docker image ls $repo/${container}:$tag | grep $container 1>$null
        if ($LASTEXITCODE -ne 0) {
            throw "$container image not found. Must build image for it to get included in the sidecar-digests file."
        }
    }

    if ($env:GITHUB_ACTIONS -eq "true" -and $null -eq $containers) {
        # remove the local image after pushing to free up disk space on the runner machine.
        Write-Host -ForegroundColor DarkGreen "Removing $repo/${container}:$tag image to make space"
        $image = docker image ls $repo/${container}:$tag --format='{{json .}}' | ConvertFrom-Json
        docker image rm $image.ID --force # Remove by ID so that all tagged references get removed.
    }

    Write-Host -ForegroundColor DarkGray "================================================================="
}

$index = 0
foreach ($artefact in $ccrArtefacts) {
    $index++
    if ($null -eq $containers -or $containers.Contains($artefact)) {
        Write-Host -ForegroundColor DarkGreen "Building $artefact container ($index/$($ccrArtefacts.Count))"
        pwsh $buildRoot/ccr/build-$artefact.ps1 -tag $tag -repo $repo -push:$push
        $anyCcrArtifactsBuilt = $true
        Write-Host -ForegroundColor DarkGray "================================================================="
    }
}

if ($env:GITHUB_ACTIONS -ne "true") {
    if ($anyCcrArtifactsBuilt) {
        pwsh $buildRoot/build-ccr-digests.ps1 `
            -repo $repo `
            -tag $tag `
            -outDir $digestFileDir `
            -push:$push `
            -skipRegoPolicy:$skipRegoPolicy
    }
}

if ($env:GITHUB_ACTIONS -ne "true") {
    if ($null -eq $containers) {
        pwsh $buildRoot/build-azcliext-cleanroom.ps1 -repo $repo -tag $tag -push:$push
    }
    pwsh $buildRoot/ccf/build-ccf-infra-containers.ps1 -tag $tag -repo $repo -push:$push -pushPolicy:$withRegoPolicy -containers:$containers
    pwsh $buildRoot/cleanroom-cluster/build-cleanroom-cluster-infra-containers.ps1 -tag $tag -repo $repo -push:$push -pushPolicy:$withRegoPolicy -containers:$containers
    pwsh $buildRoot/workloads/build-workload-infra-containers.ps1 -tag $tag -repo $repo -push:$push -pushPolicy:$withRegoPolicy -containers:$containers
}