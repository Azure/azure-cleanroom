
param(
    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$repo = "docker.io",

    [parameter(Mandatory = $false)]
    [switch]$push
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($repo) {
    $imageName = "$repo/mongodb-access:$tag"
}
else {
    $imageName = "mongodb-access:$tag"
}

docker image build -t $imageName `
    -f $PSScriptRoot/consumer/build/Dockerfile.app $PSScriptRoot/consumer
if ($push) {
    docker push $imageName
}
