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
$scriptsDir = "$root/src/k8s-node/kubelet-proxy/scripts"
$outDir = "$root/src/k8s-node/kubelet-proxy/bin"

if (!$skipBuild) {
    # ---------- Build the Go binary via Docker ----------
    Write-Host "Building kubelet-proxy binary via Docker..."
    docker image build `
        --output=$outDir --target=dist `
        -f $root/build/docker/Dockerfile.kubelet-proxy $root

    Write-Host "Binary built at $outDir/kubelet-proxy"
}

if ($push) {
    # ---------- Create and push OCI artifact ----------
    # Stage the files that make up the artifact in a temp directory so that
    # oras pushes them with flat file names (no directory prefix).
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "kubelet-proxy-oci-$tag"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Path $staging | Out-Null

    Copy-Item "$outDir/kubelet-proxy"       "$staging/kubelet-proxy"
    Copy-Item "$scriptsDir/install.sh"      "$staging/install.sh"
    Copy-Item "$scriptsDir/uninstall.sh"    "$staging/uninstall.sh"
    # Environment-specific configure.sh scripts. install.sh --env <aks|kind>
    # stages the appropriate one as configure.sh at install time.
    New-Item -ItemType Directory -Path "$staging/aks" | Out-Null
    New-Item -ItemType Directory -Path "$staging/kind" | Out-Null
    Copy-Item "$scriptsDir/aks/configure.sh"  "$staging/aks/configure.sh"
    Copy-Item "$scriptsDir/kind/configure.sh" "$staging/kind/configure.sh"

    Push-Location $staging
    try {
        Write-Host "Pushing OCI artifact to $repo/k8s-node/kubelet-proxy:$tag ..."
        oras push "$repo/k8s-node/kubelet-proxy:$tag" `
            ./kubelet-proxy `
            ./install.sh `
            ./uninstall.sh `
            ./aks/configure.sh `
            ./kind/configure.sh
        Write-Host "OCI artifact pushed successfully."
    }
    finally {
        Pop-Location
        Remove-Item -Recurse -Force $staging
    }
}
