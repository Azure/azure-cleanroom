[CmdletBinding()]
param
(
    [switch]
    $NoBuild,

    [ValidateSet('mcr', 'local', 'acr')]
    [string]$registry = "local",

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [string]
    [ValidateSet('mhsm', 'akvpremium')]
    $kvType
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
    -ccfProjectName "ob-ccf-ml-training" `
    -projectName "ob-isv-client" `
    -initialMemberName "isv" `
    -outDir $outDir
$ccfEndpoint = $(Get-Content $outDir/ccf/ccf.json | ConvertFrom-Json).endpoint
az cleanroom governance client remove --name "ob-tdp-client"
az cleanroom governance client remove --name "ob-tdc-client"

pwsh $PSScriptRoot/run-scenario-generate-template-policy.ps1 -registry $registry -repo $repo -tag $tag -ccfEndpoint $ccfEndpoint -kvType $kvType
if ($LASTEXITCODE -gt 0) {
    Write-Host -ForegroundColor Red "run-scenario-generate-template-policy returned non-zero exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

$registry_local_endpoint = ""
if ($registry -eq "local") {
    $registry_local_endpoint = "ccr-registry:5000"
}

pwsh $PSScriptRoot/../convert-template.ps1 -outDir $outDir -registry-local-endpoint $registry_local_endpoint -repo $repo -tag $tag

pwsh $PSScriptRoot/deploy-virtual-cleanroom.ps1 -repo $repo -tag $tag

# Check that expected output files got created.
$expectedFiles = @(
    "$PSScriptRoot/generated/results/output/**/model.onnx",
    "$PSScriptRoot/generated/results/application-telemetry*/**/depa-training.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/application-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/depa-training*-code-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/depa-training*-code-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/depa-training*-code-launcher.metrics",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/output*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/output*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/output*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/infrastructure-telemetry*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/cowin*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/cowin*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/cowin*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/icmr*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/icmr*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/icmr*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/index*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/index*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/index*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/model*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/model*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/model*-blobfuse-launcher.traces",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/config*-blobfuse.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/config*-blobfuse-launcher.log",
    "$PSScriptRoot/generated/results/infrastructure-telemetry*/**/config*-blobfuse-launcher.traces"
)

$missingFiles = @()
foreach ($file in $expectedFiles) {
    if (!(Test-Path $file)) {
        $missingFiles += $file
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host -ForegroundColor Red "Did not find the following expected file(s). Check clean room logs for any failure(s):"
    foreach ($file in $missingFiles) {
        Write-Host -ForegroundColor Red $file
    }
    
    exit 1
}