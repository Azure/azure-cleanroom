[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest"
)

$outDir = "$PSScriptRoot/generated"
rm -rf $outDir
Write-Host "Using $registry registry for cleanroom container images."
$root = git rev-parse --show-toplevel
pwsh $root/test/onebox/multi-party-collab/deploy-virtual-cleanroom-governance.ps1 `
    -NoBuild:$NoBuild `
    -registry $registry `
    -repo $repo `
    -tag $tag `
    -ccfProjectName "ob-ccf-query-analytics" `
    -projectName "ob-consumer-client" `
    -initialMemberName "consumer" `
    -outDir $outDir
$ccfEndpoint = $(Get-Content $outDir/ccf/ccf.json | ConvertFrom-Json).endpoint
az cleanroom governance client remove --name "ob-publisher-client"

pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 -registry $registry -repo $repo -tag $tag -ccfEndpoint $ccfEndpoint
if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "run-scenario-generate-template-policy returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}


pwsh $PSScriptRoot/../convert-template.ps1 -outDir $outDir -repo $repo -tag $tag

pwsh $PSScriptRoot/deploy-virtual-cleanroom.ps1 -repo $repo -tag $tag