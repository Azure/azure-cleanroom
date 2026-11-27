[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('container', 'cgs', 'constitution', 'cli', 'helm')]
    [string]$Kind,

    [Parameter(Mandatory)]
    [string]$Name,

    [string]$Context = '.',
    [string]$Dockerfile
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot/helpers.ps1"
$Tag = $env:GITHUB_SHA
if ($Tag -notmatch '^[a-f0-9]{40}$') {
    throw 'GITHUB_SHA must identify the exact source commit.'
}

$Payload = Join-Path (Get-Location) 'handoff/payload'
New-Item -ItemType Directory -Path $Payload -Force | Out-Null
$Image = "${Name}:$Tag"

switch ($Kind) {
    'container' {
        if ($Name -eq 'cleanroom-client') {
            & "$PSScriptRoot/build-azcliext-cleanroom.ps1" -tag $Tag -skipInstall `
                -output './src/tools/cleanroom-client/dist'
        }
        Build-DockerImage -f $Dockerfile -t $Image $Context
        docker image inspect $Image | Set-Content "$Payload/image.json" -Encoding utf8NoBOM
        docker image save --output "$Payload/$Name.tar" $Image
    }
    'cgs' {
        Build-DockerImage -f build/docker/Dockerfile.governance.ccf-app `
            --target build-image -t $Image src/governance/ccf-app/js
        # docker cp accepts a stopped container; no application process needs to run.
        $Container = docker create $Image
        try {
            docker cp "${Container}:/home/node/app/dist/bundle.json" "$Payload/bundle.json"
            docker cp "${Container}:/home/node/app/package-lock.json" "$Payload/package-lock.json"
            Copy-Item src/governance/ccf-app/js/package.json $Payload
        }
        finally {
            docker container rm $Container | Out-Null
        }
    }
    'constitution' {
        Copy-Item constitution.json "$Payload/constitution.json"
    }
    'cli' {
        & "$PSScriptRoot/build-azcliext-cleanroom.ps1" -tag $Tag -skipInstall -output $Payload
        if (-not (Get-ChildItem $Payload -Filter '*.whl')) {
            throw 'The Azure CLI extension build produced no wheel.'
        }
    }
    'helm' {
        $Version = & "$PSScriptRoot/../.github/scripts/get-helm-version.ps1" -tag $Tag
        helm package $Context --version $Version --app-version $Tag --destination $Payload
    }
}

if (-not (Get-ChildItem $Payload -File)) {
    throw "The $Name build produced no payload."
}
