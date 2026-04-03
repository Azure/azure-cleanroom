param(
    [switch]
    $NoBuild,

    [parameter(Mandatory = $false)]
    [string]$outDir = "$PSScriptRoot/generated",

    [string]
    $datastoreOutdir = "$PSScriptRoot/generated/datastores",

    [parameter(Mandatory = $true)]
    [string]$dataDir,

    [parameter(Mandatory = $false)]
    [string]$port = "8321"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel

& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    # if this command fails then prompt user to login first.
    az account show -o jsonc 1>$null
    if ($LASTEXITCODE -gt 0) {
        Write-Host -ForegroundColor Red "Azure login required. Do az login before running this script."
        exit $LASTEXITCODE
    }
}

if (!$NoBuild) {
    pwsh $root/build/ccr/build-cleanroom-client.ps1
}

# Deploy credentials-proxy container
$credproxyName = "credentials-proxy"
$credproxyHostPort = "5050"
$credproxyNetwork = "credential-proxy-bridge"
pwsh $PSScriptRoot/deploy-credentials-proxy.ps1 `
    -credproxyName $credproxyName `
    -credproxyHostPort $credproxyHostPort `
    -networkName $credproxyNetwork


mkdir -p $outDir
$containerName = "cleanroom-client"
docker rm -f $containerName 2>$null
docker run -d `
    -v ${outDir}:${outDir} `
    -v ${datastoreOutdir}:${datastoreOutdir} `
    -v ${dataDir}:${dataDir} `
    -w ${outDir} `
    -u "$(id -u $env:USER):$(id -g $env:USER)" `
    --name $containerName `
    -p ${port}:80 `
    --network $credproxyNetwork `
    -e MSI_ENDPOINT="http://${credproxyName}:8080/token" `
    -e IDENTITY_ENDPOINT="http://${credproxyName}:8080/token" `
    -e IDENTITY_HEADER="dummy_required_value" `
    -e SCRATCH_DIR="${outDir}" `
    -e AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL="$env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL" `
    -e AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL="$env:AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL" `
    -e AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL="$env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL" `
    -e AZCLI_CLEANROOM_CONTAINER_REGISTRY_USE_HTTP="$env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_USE_HTTP" `
    localhost:5000/cleanroom-client

if ($env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL.Contains("ccr-registry:5000")) {
    # If the registry is local, we need to connect the cleanroom-client container to the kind network
    # so that it can access the ccr-registry.
    docker network connect kind cleanroom-client
}

# Login to Azure using managed identity via the credentials-proxy.
$sleepTime = 5
Write-Host "Waiting for $sleepTime seconds for cleanroom-client to be up"
Start-Sleep -Seconds $sleepTime

Write-Host "Logging into Azure from cleanroom-client container..."
$loginRequest = @"
{
}
"@
curl --fail-with-body -sS -X POST http://localhost:${port}/login -H "content-type: application/json" -d $loginRequest 1>$null
Write-Host "Login successful"
