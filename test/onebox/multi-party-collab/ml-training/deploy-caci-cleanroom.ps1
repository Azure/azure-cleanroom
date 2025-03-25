[CmdletBinding()]
param
(
    [Parameter(Mandatory)]
    [string]$resourceGroup,

    [string]
    $location = "westus",

    [string]
    $outDir = "$PSScriptRoot/generated",

    [string]
    $datastoreOutdir = "$PSScriptRoot/generated/datastores",

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

do {
    Write-Host "Sleeping for 15 seconds for IP address to be available."
    Start-Sleep -Seconds 15
    $ccrIP = az container show `
        --name $cleanRoomName `
        -g $resourceGroup `
        --query "ipAddress.ip" `
        --output tsv
} while ($null -eq $ccrIP)

Write-Host "Clean Room IP address: $ccrIP"
if ($null -eq $ccrIP) {
    throw "Clean Room IP address is not set."
}

# wait for code-launcher endpoint to be up.
$timeout = New-TimeSpan -Minutes 30
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ((curl -o /dev/null -w "%{http_code}" -s -k https://${ccrIP}:8200/gov/doesnotexist/status) -ne "404") {
    Write-Host "Waiting for code-launcher endpoint to be up at https://${ccrIP}:8200"
    Start-Sleep -Seconds 3
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for code-launcher endpoint to be up."
    }
}

# The application is configured for auto-start. Hence, no need to issue the start API.
# curl -X POST -s -k https://${ccrIP}:8200/gov/depa-training/start

if (!$nowait) {
    pwsh $PSScriptRoot/wait-for-cleanroom.ps1 `
        -appName depa-training `
        -cleanroomIp $ccrIP
}

curl -v -X POST -s -k https://${ccrIP}:8200/gov/exportLogs
curl -v -X POST -s -k https://${ccrIP}:8200/gov/exportTelemetry

if (!$skiplogs) {
    mkdir -p $outDir/results
    az cleanroom datastore download `
        --config $datastoreOutdir/ml-training-consumer-datastore-config `
        --name output `
        --dst $outDir/results

    az cleanroom telemetry download `
        --cleanroom-config $outDir/configurations/tdp-config `
        --datastore-config $datastoreOutdir/ml-training-publisher-datastore-config `
        --target-folder $outDir/results

    az cleanroom logs download `
        --cleanroom-config $outDir/configurations/tdp-config `
        --datastore-config $datastoreOutdir/ml-training-publisher-datastore-config `
        --target-folder $outDir/results

    Write-Host "Application logs:"
    cat $outDir/results/application-telemetry*/**/depa-training.log
}