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

# Create credentials-proxy container unless it already exists.
# https://github.com/gsoft-inc/azure-cli-credentials-proxy
$credproxy_name = "credentials-proxy"
$credproxy_port = "5050"
$credential_proxy_image = "workleap/azure-cli-credentials-proxy:1.2.5"
if ($env:GITHUB_ACTIONS -eq "true") {
    $credential_proxy_image = "cleanroombuild.azurecr.io/workleap/azure-cli-credentials-proxy:1.2.5"
}

& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    docker network create credential-proxy-bridge 2>$null
    $state = docker inspect -f '{{.State.Running}}' "${credproxy_name}" 2>$null
    if ($state -ne "true") {
        docker run `
            -d `
            --restart=always `
            --network credential-proxy-bridge `
            -p "${credproxy_port}:8080" `
            --name "${credproxy_name}" `
            -v "/home/${env:USER}/.azure:/app/.azure/" `
            -u "$(id -u $env:USER):$(id -g $env:USER)" `
            $credential_proxy_image
    }
}

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
    --network credential-proxy-bridge `
    -e MSI_ENDPOINT="http://${credproxy_name}:8080/token" `
    -e SCRATCH_DIR="${outDir}" `
    -e AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL="$env:AZCLI_CLEANROOM_CONTAINER_REGISTRY_URL" `
    -e AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL="$env:AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL" `
    cleanroom-client

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
