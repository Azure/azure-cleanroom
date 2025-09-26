function Get-UbuntuVersion {
    if (-Not (Test-Path "/etc/os-release")) {
        throw "This script must be run on a Linux system with /etc/os-release."
    }

    $osRelease = Get-Content "/etc/os-release" | ForEach-Object {
        $parts = $_ -split '='
        if ($parts.Length -eq 2) {
            @{ Key = $parts[0]; Value = $parts[1].Trim('"') }
        }
    } | ForEach-Object { [PSCustomObject]$_ } | Group-Object Key -AsHashTable -AsString

    $name = $osRelease["NAME"].Value
    if ($name -ne "Ubuntu") {
        throw "Not running on Ubuntu. /etc/os-release NAME value is: $name"
    }

    return $osRelease["VERSION_ID"].Value
}

function Deploy-MongoDB {
    [CmdletBinding()]
    param (
        [Parameter(Mandatory = $true)]
        [string]$resourceGroup,

        [Parameter(Mandatory = $true)]
        [string]$dbSuffix,

        [Parameter()]
        [switch]$populateSampleData
    )
    $root = git rev-parse --show-toplevel
    Import-Module $root/samples/common/infra-scripts/azure-helpers.psm1 -Force -DisableNameChecking

    function Get-UniqueString ([string]$id, $length = 13) {
        $hashArray = (new-object System.Security.Cryptography.SHA512Managed).ComputeHash($id.ToCharArray())
        -join ($hashArray[1..$length] | ForEach-Object { [char]($_ % 26 + [byte][char]'a') })
    }

    $uniqueString = Get-UniqueString("${resourceGroup}-${dbSuffix}")
    $aciName = "${uniqueString}-${dbSuffix}"

    $name = "test_data"
    $user = "user"
    $password = $uniqueString

    # MongoDB image instance built from https://hub.docker.com/_/mongo/.
    Write-Host "Deploying Mongo DB ACI instance named $aciName in resource group $resourceGroup."
    # The AZ CLI for container create mandates a username and password for ACRs. The cleanroomsamples
    # ACR has anonymous pull enabled, so we will pass a random user and password to keep the CLI happy.
    az container create `
        -g $resourceGroup `
        --name $aciName `
        --image cleanroomsamples.azurecr.io/mongo:latest `
        --environment-variables MONGO_INITDB_ROOT_USERNAME=$user `
        --secure-environment-variables MONGO_INITDB_ROOT_PASSWORD=$password `
        --ports 27017 `
        --dns-name-label cl-testmongodb-$uniqueString `
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

    if ($populateSampleData) {
        # Download the MongoDB tools to the local machine.
        $toolsDir = "$root/test/onebox/multi-party-collab/mongo-db-access/tools"
        Write-Host "Downloading MongoDB tools to $toolsDir."
        $ubuntuVersion = Get-UbuntuVersion
        if ($ubuntuVersion -eq "22.04") {
            $toolsVersion = "ubuntu2204-x86_64-100.11.0"
        }
        elseif ($ubuntuVersion -eq "20.04") {
            $toolsVersion = "ubuntu2004-x86_64-100.11.0"
        }
        else {
            throw "Unsupported OS version: $ubuntuVersion"
        }

        wget https://fastdl.mongodb.org/tools/db/mongodb-database-tools-$toolsVersion.tgz -P $toolsDir
        tar -xzf $toolsDir/mongodb-database-tools-$toolsVersion.tgz -C $toolsDir
        $env:PATH = "$toolsDir/mongodb-database-tools-$toolsVersion/bin:${env:PATH}"

        $dataDir = "$root/test/onebox/multi-party-collab/mongo-db-access/publisher/input"
        mkdir -p $dataDir
        Write-Host "Downloading sample data to $dataDir."
        pwsh $PSScriptRoot/download-sample-data.ps1 -outDir $dataDir
        Write-Host "Populating sample data into the Mongo DB instance hosted at $($result.endpoint)."
        mongoimport $root/test/onebox/multi-party-collab/mongo-db-access/publisher/input/sales.json `
            --uri "mongodb://${user}:${password}@$($result.endpoint):27017" `
            --authenticationDatabase admin `
            --db test_data `
            --collection sales
    }
    else {
        Write-Host "Skipping sample data population."
    }

    return $result
}
