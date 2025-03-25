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
    $ipAddress = az container show `
        --name $aciName `
        -g $resourceGroup `
        --query "ipAddress" | ConvertFrom-Json
    $result.endpoint = $ipAddress.fqdn
    $result.ip = $ipAddress.ip

    if ($populateSampleData) {
        # Download the MongoDB tools to the local machine.
        $toolsDir = "$root/test/onebox/multi-party-collab/mongo-db-access/tools"
        Write-Host "Downloading MongoDB tools to $toolsDir."
        wget https://fastdl.mongodb.org/tools/db/mongodb-database-tools-ubuntu2204-x86_64-100.11.0.tgz -P $toolsDir
        tar -xzf $toolsDir/mongodb-database-tools-ubuntu2204-x86_64-100.11.0.tgz -C $toolsDir
        $env:PATH = "$toolsDir/mongodb-database-tools-ubuntu2204-x86_64-100.11.0/bin:${env:PATH}"

        $dataDir = "$root/test/onebox/multi-party-collab/mongo-db-access/publisher/input"
        mkdir -p $dataDir
        Write-Host "Downloading sample data to $dataDir."
        pwsh $PSScriptRoot/download-sample-data.ps1 -outDir $dataDir
        Write-Host "Populating sample data into the Mongo DB instance."
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