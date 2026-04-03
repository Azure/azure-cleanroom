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
$scriptsDir = "$root/src/k8s-node/api-server-proxy/scripts"
$outDir = "$root/src/k8s-node/api-server-proxy/bin"

if (!$skipBuild) {
    # ---------- Build the Go binary via Docker ----------
    Write-Host "Building api-server-proxy binary via Docker..."
    docker image build `
        --output=$outDir --target=dist `
        -f $root/build/docker/Dockerfile.api-server-proxy $root

    Write-Host "Binary built at $outDir/api-server-proxy"
}

if ($push) {
    # ---------- Create and push OCI artifact ----------
    # Stage the files that make up the artifact in a temp directory so that
    # oras pushes them with flat file names (no directory prefix).
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "api-server-proxy-oci-$tag"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Path $staging | Out-Null

    Copy-Item "$outDir/api-server-proxy"     "$staging/api-server-proxy"
    Copy-Item "$scriptsDir/install.sh"       "$staging/install.sh"
    Copy-Item "$scriptsDir/uninstall.sh"     "$staging/uninstall.sh"

    Push-Location $staging
    try {
        Write-Host "Pushing OCI artifact to $repo/k8s-node/api-server-proxy:$tag ..."
        oras push "$repo/k8s-node/api-server-proxy:$tag" `
            ./api-server-proxy `
            ./install.sh `
            ./uninstall.sh
        Write-Host "OCI artifact pushed successfully."
    }
    finally {
        Pop-Location
        Remove-Item -Recurse -Force $staging
    }
}
