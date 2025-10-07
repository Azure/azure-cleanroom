[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$bucketName
)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$awsAccessKeyId = az keyvault secret show  --vault-name azcleanroomemukv -n aws-access-key-id --query value -o tsv
$awsSecretAccessKey = az keyvault secret show  --vault-name azcleanroomemukv -n aws-secret-access-key --query value -o tsv
$awsDefaultRegion = "us-west-1"
$awsCliImage = "cleanroomsamples.azurecr.io/aws-cli:2.27.62"
$script:bucketExists = $false
& {
    # Disable $PSNativeCommandUseErrorActionPreference for this scriptblock
    $PSNativeCommandUseErrorActionPreference = $false
    docker run --rm `
        --env "AWS_ACCESS_KEY_ID=$awsAccessKeyId" `
        --env "AWS_SECRET_ACCESS_KEY=$awsSecretAccessKey" `
        --env "AWS_DEFAULT_REGION=$awsDefaultRegion" `
        $awsCliImage `
        s3api head-bucket --bucket $bucketName 1>$null 2>$null
    if ($LASTEXITCODE -eq 0) {
        $script:bucketExists = $true
    }
}

if ($script:bucketExists) {
    Write-Output "Deleting bucket $bucketName."
    docker run --rm `
        --env "AWS_ACCESS_KEY_ID=$awsAccessKeyId" `
        --env "AWS_SECRET_ACCESS_KEY=$awsSecretAccessKey" `
        --env "AWS_DEFAULT_REGION=$awsDefaultRegion" `
        $awsCliImage `
        s3 rb s3://$bucketName --force
}