param(
    [switch]$push
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
. $root/build/helpers.ps1

$imageName = "accr-conf-inferencing-provider"
$tag = "latest"

# The Dockerfile needs the airunway controller source.
# Create a temp build context that includes both repos.
$buildContext = Join-Path ([System.IO.Path]::GetTempPath()) "accr-provider-build"
if (Test-Path $buildContext) {
    Remove-Item -Recurse -Force $buildContext
}
New-Item -ItemType Directory -Path $buildContext | Out-Null

# Copy only what the Dockerfile needs.
$srcOp = "$root/src/cleanroom-operator"
$dstOp = "$buildContext/src/cleanroom-operator"
New-Item -ItemType Directory -Path $dstOp -Force | Out-Null
New-Item -ItemType Directory -Path "$dstOp/providers/airunway" -Force | Out-Null
Copy-Item "$srcOp/go.mod", "$srcOp/go.sum" -Destination $dstOp
Copy-Item "$srcOp/api" -Destination $dstOp -Recurse
Copy-Item "$srcOp/providers/airunway/*" -Destination "$dstOp/providers/airunway" -Recurse

# Rewrite the airunway replace directive for the Docker
# build context layout (/app/cleanroom-operator/providers/
# airunway → /app/airunway/controller).
$gomod = "$dstOp/providers/airunway/go.mod"
(Get-Content $gomod -Raw) -replace `
    'replace github.com/kaito-project/airunway/controller => .+', `
    'replace github.com/kaito-project/airunway/controller => ../../../airunway/controller' |
Set-Content $gomod -NoNewline

# Copy airunway controller source.
$airunwaySrc = "$root/../airunway/controller"
$airunwayDst = "$buildContext/airunway/controller"
New-Item -ItemType Directory -Path $airunwayDst -Force | Out-Null
Copy-Item "$airunwaySrc/go.mod", "$airunwaySrc/go.sum" -Destination $airunwayDst
Copy-Item "$airunwaySrc/api" -Destination $airunwayDst -Recurse

Write-Host "Building $imageName..."

docker image build -t "${imageName}:${tag}" `
    -f "$root/build/docker/Dockerfile.accr-conf-inferencing-provider" `
    "$buildContext"

if ($push) {
    $registry = Get-LocalRegistryUrl
    docker tag "${imageName}:${tag}" "${registry}/${imageName}:${tag}"
    docker push "${registry}/${imageName}:${tag}"
    Write-Host "Pushed ${registry}/${imageName}:${tag}"
}
