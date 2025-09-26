function Deploy-PostgreSQL {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory = $true)]
        [string]$resourceGroup
    )
    $root = git rev-parse --show-toplevel

    function Get-UniqueString ([string]$id, $length = 13) {
        $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
        -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
    }

    $uniqueString = Get-UniqueString("${resourceGroup}")
    $aciName = "${uniqueString}-db"

    $name = "test_data"
    $user = "user"
    $password = $uniqueString

    # PostgreSQL image instance built from https://github.com/TonicAI/docker-testdb.
    Write-Host "Deploying PostgreSQL ACI instance named $aciName in resource group $resourceGroup."
    # The AZ CLI for container create mandates a username and password for ACRs. The cleanroomsamples
    # ACR has anonymous pull enabled, so we will pass a random user and password to keep the CLI happy.
    az container create `
        -g $resourceGroup `
        --name $aciName `
        --image cleanroomsamples.azurecr.io/testdb_postgres:latest `
        --environment-variables POSTGRES_USER=$user POSTGRES_DB=$name `
        --secure-environment-variables POSTGRES_PASSWORD=$password `
        --ports 5432 `
        --dns-name-label cl-testdb-$uniqueString `
        --os-type Linux `
        --cpu 1 `
        --memory 1 `
        --registry-username "anonymous" `
        --registry-password "*"

    $result = @{
        endpoint = ""
        ip       = ""
        name     = $name
        user     = $user
        password = $password
    }
    $timeout = New-TimeSpan -Minutes 15
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    do {
        Write-Host "Sleeping for 15 seconds for IP address to be available."
        Start-Sleep -Seconds 15
        $ipAddress = az container show `
            --name $aciName `
            -g $resourceGroup `
            --query "ipAddress" | ConvertFrom-Json
        if ($stopwatch.elapsed -gt $timeout) {
            throw "Hit timeout waiting for IP address to be available."
        }
    } while ($null -eq $ipAddress.ip)

    $result.endpoint = $ipAddress.fqdn
    $result.ip = $ipAddress.ip
    return $result
}