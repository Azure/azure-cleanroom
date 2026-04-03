param(

    [string]$repo = "localhost:5000",

    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [string]$port = "8321"
)

#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$containerName = "local-idp"
docker rm -f $containerName 2>$null

# Try to pull the image, build if not found
& {
    $PSNativeCommandUseErrorActionPreference = $false
    docker pull $repo/local-idp:$tag
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Image not found, building local-idp..."
        $root = git rev-parse --show-toplevel
        pwsh "$root/build/ccr/build-local-idp.ps1" -repo $repo -tag $tag
    }
}

docker run -d `
    --name $containerName `
    -p ${port}:8399 `
    $repo/local-idp:$tag