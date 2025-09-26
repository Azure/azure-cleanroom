
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
    $imageName = "$repo/analytics:$tag"
}
else {
    $imageName = "analytics:$tag"
}

docker image build -t $imageName `
    -f $PSScriptRoot/application/Dockerfile.analytics $PSScriptRoot/application
if ($push) {
    docker push $imageName
}
