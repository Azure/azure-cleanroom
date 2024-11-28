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
    $datastoreOutdir = "$PSScriptRoot/../generated/datastores",

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
    pwsh $root/test/onebox/multi-party-collab/wait-for-cleanroom.ps1 `
        -resourceGroup $resourceGroup `
        -cleanRoomName $cleanRoomName
}

if (!$skiplogs) {
    mkdir -p $outDir/results
    mkdir -p $outDir/results-decrypted
    az cleanroom datastore download `
        --config $datastoreOutdir/encrypted-storage-consumer-datastore-config `
        --name consumer-output `
        --dst $outDir/results

    az cleanroom telemetry download `
        --cleanroom-config $outDir/configurations/publisher-config `
        --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
        --target-folder $outDir/results

    az cleanroom logs download `
        --cleanroom-config $outDir/configurations/publisher-config `
        --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
        --target-folder $outDir/results

    az cleanroom datastore decrypt `
        --config $datastoreOutdir/encrypted-storage-consumer-datastore-config `
        --name consumer-output `
        --source-path $outDir/results `
        --destination-path $outDir/results-decrypted

    az cleanroom logs decrypt `
        --cleanroom-config $outDir/configurations/publisher-config `
        --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
        --source-path $outDir/results `
        --destination-path $outDir/results-decrypted
        
    az cleanroom telemetry decrypt `
        --cleanroom-config $outDir/configurations/publisher-config `
        --datastore-config $datastoreOutdir/encrypted-storage-publisher-datastore-config `
        --source-path $outDir/results `
        --destination-path $outDir/results-decrypted        

    Write-Host "Application logs:"
    cat $outDir/results-decrypted/application-telemetry*/demo-app.log
}