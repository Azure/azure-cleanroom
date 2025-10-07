[CmdletBinding()]
param
(
    [Parameter(Mandatory)]
    [string]$resourceGroup,

    [string]
    $location = "westeurope",

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
    
    # wait for pyspark endpoint to be up.
    $timeout = New-TimeSpan -Minutes 20
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ((curl -o /dev/null -w "%{http_code}" -s -k https://${ccrIP}:8310/app/run_query/12) -ne "200") {
        Write-Host "Waiting for pyspark endpoint to be up at https://${ccrIP}:8310"
        Start-Sleep -Seconds 3
        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for pyspark endpoint to be up."
        }
    }

    Write-Host "Successfully connected to the pyspark endpoint at https://${ccrIP}:8310"
}

curl -v -X POST -s -k https://${ccrIP}:8200/gov/exportLogs
curl -v -X POST -s -k https://${ccrIP}:8200/gov/exportTelemetry

if (!$skiplogs) {
    mkdir -p $outDir/results
    az cleanroom datastore download `
        --config $outDir/configurations/consumer-config `
        --name consumer-output `
        --dst $outDir/results

    az cleanroom telemetry download `
        --cleanroom-config $outDir/configurations/publisher-config `
        --datastore-config $outDir/datastores/analytics-publisher-datastore-config `
        --target-folder $outDir/results

    az cleanroom logs download `
        --cleanroom-config $outDir/configurations/publisher-config `
        --datastore-config $outDir/datastores/analytics-publisher-datastore-config `
        --target-folder $outDir/results

    Write-Host "Application logs:"
    cat $outDir/results/application-telemetry*/**/*.log
}