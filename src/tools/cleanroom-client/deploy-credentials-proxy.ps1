[CmdletBinding()]
param(
    [string]$credproxyName = "credentials-proxy",
    [string]$credproxyHostPort = "5050",
    [string]$networkName = "credential-proxy-bridge"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Create credentials-proxy container unless it already exists.
# https://github.com/gsoft-inc/azure-cli-credentials-proxy
$credential_proxy_image = "workleap/azure-cli-credentials-proxy:1.2.5"
if ($env:GITHUB_ACTIONS -eq "true") {
    $credential_proxy_image = "cleanroombuild.azurecr.io/workleap/azure-cli-credentials-proxy:1.2.5"
}

& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    
    Write-Host "Creating Docker network '$networkName' if it doesn't exist..."
    docker network create $networkName 2>$null
    
    $state = docker inspect -f '{{.State.Running}}' "${credproxyName}" 2>$null
    if ($state -ne "true") {
        Write-Host "Starting credentials-proxy container '$credproxyName' on port $credproxyHostPort..."
        docker run `
            -d `
            --restart=always `
            --network $networkName `
            -p "${credproxyHostPort}:8080" `
            --name "${credproxyName}" `
            -v "/home/${env:USER}/.azure:/app/.azure/" `
            -u "$(id -u $env:USER):$(id -g $env:USER)" `
            $credential_proxy_image
        Write-Host "Credentials-proxy container started successfully."
    }
    else {
        Write-Host "Credentials-proxy container '$credproxyName' is already running."
    }
}
