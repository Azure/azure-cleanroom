param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push,

    [string]$outDir = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. $PSScriptRoot/../helpers.ps1

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
    mkdir -p $sandbox_common
}
else {
    $sandbox_common = $outDir
}

# See https://docs.docker.com/build/guide/export/ for --output usage reference.
docker image build `
    --output=$sandbox_common --target=dist `
    -f $buildRoot/docker/Dockerfile.governance.ccf-app "$root/src/governance/ccf-app/js"

if ($push) {
    if ($env:GITHUB_ACTIONS -eq "true") {
        # If running in github actions then add a unique string into the js app bundle so that even
        # if there is no code change we get a unique bundle digest so that each github action run
        # generates a unique hash value.
        Write-Host "Inserting unique string value '$tag' in the jsapp bundle."
        $bundle_content = jq --arg tag1 "const github_actions_jsapp_tag = ""$tag"";`n" '.modules |= [{"name": "__github_actions_jsapp_tag.js__", "module": $tag1}] + .' "$sandbox_common/bundle.json"
        $bundle_content | Out-File "$sandbox_common/bundle.json"
    }

    $bundleDigest = cat "$sandbox_common/bundle.json" | jq -S -j | sha256sum | cut -d ' ' -f 1

    Push-Location $sandbox_common
    oras push "$repo/cgs-js-app:$tag,$bundleDigest" ./bundle.json `
        --annotation "cleanroom.version=$tag" `
        --annotation "bundle.json.digest=sha256:$bundleDigest"
    Pop-Location

    $digest = Get-Digest -repo $repo -containerName "cgs-js-app" -tag $tag
    $jsApp = @"
cgs-js-app:
  version: $tag
  image: $repo/cgs-js-app@$digest
"@
    $jsApp | Out-File "$sandbox_common/version.yaml"

    Push-Location $sandbox_common
    oras push "$repo/versions/cgs-js-app:latest" ./version.yaml
    Pop-Location

    $ccfConstitutionDir = "$root/src/ccf/ccf-provider-common/constitution"
    $cgsConstitutionDir = "$root/src/governance/ccf-app/js/constitution"
    $content = ""
    Get-ChildItem $ccfConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }
    Get-ChildItem $cgsConstitutionDir -Filter *.js | Foreach-Object { $content += Get-Content $_.FullName -Raw }

    # If running in github actions then add a unique string into the constitution so that even
    # if there is no code change we get a unique constitution digest so that each github action run
    # generates a unique hash value.
    if ($env:GITHUB_ACTIONS -eq "true") {
        Write-Host "Inserting unique string value '$tag' in the constitution."
        $content += "const github_actions_tag = ""$tag"";"
    }

    $content | Out-File "$sandbox_common/constitution.js"
    $content | ConvertTo-Json | Out-File "$sandbox_common/constitution.json"
    $constitutionDigest = cat "$sandbox_common/constitution.json" | jq -S -j | sha256sum | cut -d ' ' -f 1

    Push-Location $sandbox_common
    oras push "$repo/cgs-constitution:$tag,$constitutionDigest" ./constitution.json `
        --annotation "cleanroom.version=$tag" `
        --annotation "constitution.js.digest=sha256:$constitutionDigest"
    Pop-Location

    $digest = Get-Digest -repo $repo -containerName "cgs-constitution" -tag $tag
    $constitution = @"
cgs-constitution:
  version: $tag
  image: $repo/cgs-constitution@$digest
"@
    $constitution | Out-File "$sandbox_common/version.yaml"
    Push-Location $sandbox_common
    oras push "$repo/versions/cgs-constitution:latest" ./version.yaml
    Pop-Location
}