[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$resourceGroup,

    [Parameter(Mandatory = $true)]
    [string]$bucketName,

    [Parameter(Mandatory = $true)]
    [string]$outDir
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$aciName = "${resourceGroup}-s3ninja"

Write-Host "Deploying S3 Ninja ACI instance named $aciName in resource group $resourceGroup."
# The AZ CLI for container create mandates a username and password for ACRs. The cleanroomsamples
# ACR has anonymous pull enabled, so we will pass a random user and password to keep the CLI happy.
az container create `
    -g $resourceGroup `
    --name $aciName `
    --image cleanroomsamples.azurecr.io/s3-ninja:sonarqube-20250715-1842 `
    --ports 9000 `
    --dns-name-label $aciName `
    --os-type Linux `
    --cpu 1 `
    --memory 2 `
    --registry-username "anonymous" `
    --registry-password "*"

if ($null -eq $ipAddress.ip) {
    $timeout = New-TimeSpan -Minutes 15
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        Write-Host "Sleeping for 15 seconds for IP address to be available."
        Start-Sleep -Seconds 15
        $ipAddress = az container show `
            --name $aciName `
            -g $resourceGroup `
            --query "ipAddress" | ConvertFrom-Json
        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for IP address to be available."
        }
    } while ($null -eq $ipAddress.ip)
}

@"
{
    "endpoint": "http://$($ipAddress.fqdn):9000/s3"
}
"@ > $outDir/s3ninja.json

mkdir -p $outDir/.aws
@"
[profile s3ninja]
aws_access_key_id = AKIAIOSFODNN7EXAMPLE
aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
region = us-east-1
"@ > $outDir/.aws/config

Write-Output "Creating bucket $bucketName."
docker run --rm `
    -v $outDir/.aws:/root/.aws cleanroomsamples.azurecr.io/aws-cli:2.27.62 --endpoint-url http://$($ipAddress.fqdn):9000/s3 --profile s3ninja s3 mb s3://$bucketName
