function Create-Storage-Resources {
    param(
        [string]$resourceGroup,

        [string[]]$storageAccountNames,

        [string]$objectId,

        [switch]$enableHns,

        [switch]$allowSharedKeyAccess,

        [string]$kind = "StorageV2"
    )

    foreach ($storageAccountName in $storageAccountNames) {
        Write-Host "Creating storage account $storageAccountName in resource group $resourceGroup"
        $result = (az storage account create `
                --name $storageAccountName `
                --resource-group $resourceGroup `
                --min-tls-version TLS1_2 `
                --allow-shared-key-access $allowSharedKeyAccess `
                --kind $kind `
                --enable-hierarchical-namespace $enableHns)
        $storageAccountResult = $result | ConvertFrom-Json

        Write-Host "Assigning 'Storage Blob Data Contributor' permissions to logged in user"
        az role assignment create --role "Storage Blob Data Contributor" --scope $storageAccountResult.id --assignee-object-id $objectId --assignee-principal-type $(Get-Assignee-Principal-Type)
        $storageAccountResult

        if ($env:GITHUB_ACTIONS -eq "true") {
            & {
                # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
                $PSNativeCommandUseErrorActionPreference = $false
                $timeout = New-TimeSpan -Seconds 120
                $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
                $hasAccess = $false
                while (!$hasAccess) {
                    # Do an exists check to determine whether the permissions have been applied or not.
                    az storage container create --name ghaction-c --account-name $storageAccountName --auth-mode login
                    az storage blob upload --data "teststring" --overwrite -c ghaction-c -n ghaction-b --account-name $storageAccountName --auth-mode login
                    if ($LASTEXITCODE -gt 0) {
                        if ($stopwatch.elapsed -gt $timeout) {
                            throw "Hit timeout waiting for rbac permissions to be applied on the storage account."
                        }
                        $sleepTime = 10
                        Write-Host "Waiting for $sleepTime seconds before checking if permissions got applied..."
                        Start-Sleep -Seconds $sleepTime
                    }
                    else {
                        Write-Host "Blob creation check returned $LASTEXITCODE. Assuming permissions got applied."
                        $hasAccess = $true
                    }
                }
            }
        }
    }
}

function Get-Assignee-Principal-Type {
    if ($env:GITHUB_ACTIONS -eq "true") {
        return "ServicePrincipal"
    }
    else {
        return "User"
    }
}
