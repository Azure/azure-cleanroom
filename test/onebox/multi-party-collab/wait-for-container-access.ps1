param(
    [Parameter(Mandatory = $true)]
    [string]$containerName,

    [Parameter(Mandatory = $true)]
    [string]$storageAccountId
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($env:GITHUB_ACTIONS -eq "true") {
    & {
        # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
        $PSNativeCommandUseErrorActionPreference = $false
        $timeout = New-TimeSpan -Seconds 120
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $hasAccess = $false
        $storageAccountName = $storageAccountId.Split("/")[-1]
        while (!$hasAccess) {
            # Do an upload to determine whether the permissions have been applied or not.
            az storage blob upload `
                --data "teststring" `
                --overwrite `
                -c $containerName `
                -n ghaction-b `
                --account-name $storageAccountName `
                --auth-mode login 1>$null 2>$null
            if ($LASTEXITCODE -gt 0) {
                if ($stopwatch.elapsed -gt $timeout) {
                    throw "Hit timeout waiting for rbac permissions to be applied on the storage account $storageAccountName for container $containerName."
                }
                $sleepTime = 10
                Write-Host "Waiting for $sleepTime seconds before checking if permissions got applied on container $containerName in account $storageAccountName..."
                Start-Sleep -Seconds $sleepTime
            }
            else {
                Write-Host "Blob creation check returned $LASTEXITCODE. Assuming permissions got applied on container $containerName in account $storageAccountName."
                $hasAccess = $true
            }
        }
    }
}
