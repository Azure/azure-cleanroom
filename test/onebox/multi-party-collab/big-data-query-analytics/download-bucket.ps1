[CmdletBinding()]
param (
    [Parameter(Mandatory = $true)]
    [string]$bucketName,

    [Parameter(Mandatory = $true)]
    [string]$dst

)

# https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$awsAccessKeyId = az keyvault secret show  --vault-name azcleanroomemukv -n aws-access-key-id --query value -o tsv
$awsSecretAccessKey = az keyvault secret show  --vault-name azcleanroomemukv -n aws-secret-access-key --query value -o tsv
$awsDefaultRegion = "us-west-1"
$awsCliImage = "cleanroomsamples.azurecr.io/aws-cli:2.27.62"

docker run --rm `
    --env "AWS_ACCESS_KEY_ID=$awsAccessKeyId" `
    --env "AWS_SECRET_ACCESS_KEY=$awsSecretAccessKey" `
    --env "AWS_DEFAULT_REGION=$awsDefaultRegion" `
    -v "${dst}:/data" `
    $awsCliImage `
    s3 cp s3://$bucketName /data --recursive
