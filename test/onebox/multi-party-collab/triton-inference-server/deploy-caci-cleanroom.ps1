[CmdletBinding()]
param
(
    [Parameter(Mandatory)]
    [string]$resourceGroup,

    [string]
    $location = "westus",

    [string]
    $outDir = "$PSScriptRoot/generated",

    [switch]
    $nowait,

    [switch]
    $skiplogs
)

$root = git rev-parse --show-toplevel

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
    --parameters location=$location

if (!$nowait) {
    $ccrIP = az container show `
        --name $cleanRoomName `
        -g $resourceGroup `
        --query "ipAddress.ip" `
        --output tsv
    Write-Host "Clean Room IP address: $ccrIP"
    if ($null -eq $ccrIP) {
        throw "Clean Room IP address is not set."
    }
    
    $caCert = "$outDir/cleanroomca.crt"
    $base64CaCert = $(cat $caCert | base64 -w 0)

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

    $timeout = New-TimeSpan -Minutes 20
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ((curl -o /dev/null -w "%{http_code}" -s --proxy http://127.0.0.1:10080 http://ccr.cleanroom.local:8000/v2/health/ready) -ne "200") {
        Write-Host "Waiting for tritonserver endpoint to be up at http://ccr.cleanroom.local:8000/v2/health/ready ($($stopwatch.elapsed.ToString()))"
        Start-Sleep -Seconds 20
        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for tritonserver endpoint to be up."
        }
    }
    $stopwatch.Stop()
    Write-Host "Waited for ($($stopwatch.elapsed.ToString()))"

    # Run the image_client going via the above http proxy.
    $tritonServerImage = "nvcr.io/nvidia/tritonserver:22.05-py3"
    $tritonServerSdkImage = $tritonServerImage + "-sdk"
    docker run --rm --net=host $tritonServerSdkImage /bin/bash -c `
        "http_proxy=http://127.0.0.1:10080 ./install/bin/image_client -m densenet_onnx -c 3 -s INCEPTION /workspace/images/mug.jpg -u http://ccr.cleanroom.local:8000"
}

if (!$skiplogs) {
    # TODO (gsinha): Add logic to download logs
    # mkdir -p $outDir/results
}