[CmdletBinding()]
param (
    [Parameter(Mandatory=$true)]
    [Alias("resource-group")]
    [string]$resourceGroup,

    [Parameter(Mandatory=$true)]
    [Alias("aci-name")]
    [string]$aciName,

    [Parameter(Mandatory=$true)]
    [Alias("container-name")]
    [string]$containerName
)

$containerInfo = az container show -n $aciName -g $resourceGroup | ConvertFrom-Json
$testContainer = $containerInfo.containers | Where-Object -Property "name" -EQ $containerName

while($testContainer.instanceView.currentState.state -ne "Terminated")
{
    Write-Host "Waiting for test to complete."
    Start-Sleep -Seconds 10
    $containerInfo = az container show -n $aciName -g $resourceGroup | ConvertFrom-Json
    $testContainer = $containerInfo.containers | Where-Object -Property "name" -EQ $containerName
}

$exitCode = $testContainer.instanceView.currentState.exitCode

if ($exitCode -eq 0)
{
    Write-Host "Tests succeeded!"
}
else
{
    throw "Tests failed!"
}