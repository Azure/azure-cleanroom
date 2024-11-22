[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [Alias("test-id")]
    [string]$testId,

    [Parameter(Mandatory = $true)]
    [Alias("storage-account-name")]
    [string]$storageAccountName,

    [Parameter(Mandatory = $true)]
    [Alias("container-name")]
    [string]$containerName,

    [Parameter(Mandatory = $false)]
    [Alias("dotnet-test-filter")]
    [string]$filter,

    [switch]
    [Alias("donot-terminate")]
    $donotTerminate
)

Write-Host "Running tests..."
dotnet test /app/UnitTests.dll --logger "trx;LogFileName=TestRunResult$testId.trx" --filter "$filter"

Write-Host "Logging into Azure"
az login --identity

Write-Host "Uploading test results to container $containerName in storage account $storageAccountName."
az storage blob upload --file "/app/TestResults/TestRunResult$testId.trx"  --account-name $storageAccountName --container-name $containerName --auth-mode login --overwrite

if ($donotTerminate -eq $true) {
    Write-Host "Sleeping in script as don't terminate option was specified"
    sleep 10000
}