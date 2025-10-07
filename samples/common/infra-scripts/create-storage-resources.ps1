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

        $role = "Storage Blob Data Contributor"
        $roleAssignment = (az role assignment list `
                --assignee-object-id $objectId `
                --scope $storageAccountResult.id `
                --role $role `
                --fill-principal-name false `
                --fill-role-definition-name false) | ConvertFrom-Json

        if ($roleAssignment.Length -eq 1) {
            Write-Host "$role permission on the storage account already exists, skipping assignment"
        }
        else {
            Write-Host "Assigning '$role' permissions to logged in user"
            az role assignment create --role $role --scope $storageAccountResult.id --assignee-object-id $objectId --assignee-principal-type $(Get-Assignee-Principal-Type)
        }
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
                    az storage container create --name ghaction-c --account-name $storageAccountName --auth-mode login 1>$null 2>$null
                    az storage blob upload --data "teststring" --overwrite -c ghaction-c -n ghaction-b --account-name $storageAccountName --auth-mode login 1>$null 2>$null
                    if ($LASTEXITCODE -gt 0) {
                        if ($stopwatch.elapsed -gt $timeout) {
                            throw "Hit timeout waiting for rbac permissions to be applied on the storage account."
                        }
                        $sleepTime = 10
                        Write-Host "Waiting for $sleepTime seconds before checking if storage account permissions got applied..."
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
