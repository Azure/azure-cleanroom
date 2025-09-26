[CmdletBinding()]
param
(
    [Parameter(Mandatory)]
    [string]$resourceGroup,

    [string]
    $location = "westeurope",

    [string]
    $outDir = "$PSScriptRoot/generated",

    [string]
    $dnsNameLabel = ""
)

$root = git rev-parse --show-toplevel

pwsh $root/build/ccr/build-ccr-client-proxy.ps1

function Get-UniqueString ([string]$id, $length = 13) {
    $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
    -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
}

$uniqueString = Get-UniqueString("${resourceGroup}")
$cleanRoomName = "${uniqueString}-cl"
Write-Host "Deploying clean room $cleanRoomName in resource group $resourceGroup"
az deployment group create `
    --resource-group $resourceGroup `
    --name $cleanRoomName `
    --template-file "$outDir/deployments/cleanroom-arm-template.json" `
    --parameters location=$location dnsNameLabel=$dnsNameLabel

$timeout = New-TimeSpan -Minutes 15
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
do {
    Write-Host "Sleeping for 15 seconds for IP address to be available."
    Start-Sleep -Seconds 15
    $ccrIP = az container show `
        --name $cleanRoomName `
        -g $resourceGroup `
        --query "ipAddress.ip" `
        --output tsv
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for IP address to be available."
    }
} while ($null -eq $ccrIP)

Write-Host "Clean Room IP address: $ccrIP"
if ($null -eq $ccrIP) {
    throw "Clean Room IP address is not set."
}

$ccrFqdn = ""
if ($dnsNameLabel -ne "") {
    $ccrFqdn = az container show `
        --name $cleanRoomName `
        -g $resourceGroup `
        --query "ipAddress.fqdn" `
        --output tsv
    if ($ccrFqdn -eq "") {
        throw "Expecting FQDN property to be populated on the CACI instance but found empty value."
    }
}

$base64CaCert = $(cat $outDir/cleanroomca.crt | base64 -w 0)

# wait for code-launcher endpoint to be up.
$timeout = New-TimeSpan -Minutes 15
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ((curl -o /dev/null -w "%{http_code}" -s -k https://${ccrIP}:8200/gov/doesnotexist/status) -ne "404") {
    Write-Host "Waiting for code-launcher endpoint to be up at https://${ccrIP}:8200"
    Start-Sleep -Seconds 15
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for code-launcher endpoint to be up."
    }
}

Write-Host "Code launcher is up."

# Do a get status and record response.
$expectedResponse = '{"detail":"Application not found"}'
$response = curl -s -k https://${ccrIP}:8200/gov/doesnotexist/status
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response. ExpectedResponse: $expectedResponse."
    exit 1
}

# If an FQDN was generated then the above query should also be reachable without "-k" option and instead specifying the cacert.
if ($ccrFqdn -ne "") {
    Write-Host "Testing direct endpoint access via FQDN '$ccrFqdn' using curl --cacert option"
    $response = curl -s --cacert $outDir/cleanroomca.crt https://${ccrFqdn}:8200/gov/doesnotexist/status
    if ($response -ne $expectedResponse) {
        Write-Host -ForegroundColor Red "Did not get expected response. Received: $response. ExpectedResponse: $expectedResponse."
        exit 1
    }
}

# Start the ccr-client-proxy that listens on 10080 and handles HTTPS connection to the cleanroom endpoint.
# --add-host parameter is used below so that subject alt. name wildcard entry of *.cleanroom.local
# in the server cert that the clean room presents passes SSL verification perfomed by envoy.
# The image_client must use "ccr.cleanroom.local" so that the hostname verification with the server cert works.
docker stop ccr-client-proxy 1>$null 2>$null
docker rm ccr-client-proxy 1>$null 2>$null
docker run `
    --name ccr-client-proxy `
    -d `
    --add-host=ccr.cleanroom.local:${ccrIP} `
    -p 10080:10080 `
    -e CA_CERT=$base64CaCert `
    ccr-client-proxy `
    /bin/bash -c ./bootstrap.sh

# Need to wait a bit for the proxy to start.
bash $root/src/scripts/wait-for-it.sh --timeout=20 --strict 127.0.0.1:10080 -- echo "ccr-client-proxy is available"
Start-Sleep -Seconds 5

# below should fail
$expectedResponse = '{"code":"RequestNotAllowed","message":"Failed ccr policy check: Requested API is not allowed"}'
$response = curl -s http://ccr.cleanroom.local:8200/gov/blah --proxy http://127.0.0.1:10080
if ($response -ne $expectedResponse) {
    Write-Host -ForegroundColor Red "Did not get expected response. Received: $response."
    exit 1
}