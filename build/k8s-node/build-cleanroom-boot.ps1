param(
    [parameter(Mandatory = $false)]
    [string]$repo = "localhost:5000",

    [parameter(Mandatory = $false)]
    [string]$tag = "latest",

    [parameter(Mandatory = $false)]
    [switch]$skipBuild,

    [parameter(Mandatory = $false)]
    [switch]$push
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$outDir = "$root/src/k8s-node/cleanroom-boot/dist"

if (!$skipBuild) {
    # ---------- Build the Go binary via Docker ----------
    Write-Host "Building cleanroom-boot Go binary via Docker..."

    # Clean previous build output.
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Path $outDir | Out-Null

    docker image build `
        --output=$outDir --target=dist `
        -f $root/build/docker/Dockerfile.cleanroom-boot $root

    Write-Host "Binary built at $outDir/cleanroom-boot"
}

if ($push) {
    # ---------- Create and push OCI artifact ----------
    # Stage the binary in a temp directory so that oras pushes it with
    # a flat file name (no directory prefix).
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "cleanroom-boot-oci-$tag"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Path $staging | Out-Null

    Copy-Item "$outDir/cleanroom-boot" "$staging/cleanroom-boot"

    Push-Location $staging
    try {
        Write-Host "Pushing OCI artifact to $repo/k8s-node/cleanroom-boot:$tag ..."
        oras push "$repo/k8s-node/cleanroom-boot:$tag" ./cleanroom-boot
        Write-Host "OCI artifact pushed successfully."
    }
    finally {
        Pop-Location
        Remove-Item -Recurse -Force $staging
    }
}
