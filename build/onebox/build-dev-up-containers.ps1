[CmdletBinding()]
param
(
    [string]$repo = "localhost:5000",

    [string]
    [ValidateSet("cached", "cached-debug", "allow-all")]
    $securityPolicyCreationOption = "allow-all",

    [string]$outDir = ""
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$build = "$root/build"

. $root/build/helpers.ps1

if ($outDir -eq "") {
    $sandbox_common = "$PSScriptRoot/sandbox_common"
}
else {
    $sandbox_common = $outDir
}

mkdir -p $sandbox_common

# ---------------------------------------------------------------------------
# Resolve or generate the build tag
# ---------------------------------------------------------------------------
$lastTagFile = "$sandbox_common/.last-tag"
if (Test-Path $lastTagFile) {
    $tag = (Get-Content $lastTagFile -Raw).Trim()
    Write-Host "Reusing existing tag from ${lastTagFile}: $tag"
}
else {
    $tag = "100.$(Get-Date -UFormat %s)"
    $tag | Out-File -FilePath $lastTagFile -Encoding utf8 -NoNewline
    Write-Host "Generated new tag: $tag (saved to ${lastTagFile})"
}

# ---------------------------------------------------------------------------
# Build images
# ---------------------------------------------------------------------------
$pushPolicy = $securityPolicyCreationOption -ne "allow-all"
pwsh $build/onebox/build-containers.ps1 `
    -repo $repo -tag $tag -withRegoPolicy:$pushPolicy

# ---------------------------------------------------------------------------
# Generate env file
# ---------------------------------------------------------------------------
pwsh $PSScriptRoot/generate-dev-up-env.ps1 `
    -repo $repo -tag $tag -outDir $sandbox_common

Write-Host -ForegroundColor Green "Build complete."
