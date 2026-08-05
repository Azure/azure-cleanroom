# install-cli.ps1 - Install kubectl-cleanroom from an OCI registry.
#
# Usage:
#   pwsh install-cli.ps1 -repo <registry> -tag <version> [-installLocation <path>]
#
# Examples:
#   pwsh install-cli.ps1 -repo myregistry.azurecr.io -tag 0.20.0
#   pwsh install-cli.ps1 -repo localhost:5000 -tag latest -installLocation ~/bin/kubectl-cleanroom

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$repo,

    [Parameter(Mandatory = $true)]
    [string]$tag,

    [string]$installLocation = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$artifactName = "kubectl-cleanroom-binary"

# Default install location.
if ($installLocation -eq "") {
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        $installLocation = Join-Path $env:USERPROFILE `
            ".local\bin\kubectl-cleanroom.exe"
    }
    else {
        $installLocation = Join-Path $env:HOME `
            ".local/bin/kubectl-cleanroom"
    }
}

# Check for Docker (needed later by dev up / operator).
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Warning "Docker is not installed or not on PATH."
    Write-Warning "Docker is required to run the management cluster."
    Write-Warning "Install it from https://docs.docker.com/get-docker/"
}

$orasVersion = "1.2.2"
$artifact = "$repo/${artifactName}:$tag"
$tmpDir = Join-Path ([System.IO.Path]::GetTempPath()) `
([System.Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

try {
    # Ensure oras is available; download a temporary copy if not found.
    if (Get-Command oras -ErrorAction SilentlyContinue) {
        $orasBin = "oras"
    }
    else {
        Write-Host "oras CLI not found; downloading a temporary copy..."
        if ($IsWindows -or $env:OS -eq "Windows_NT") {
            $orasOs = "windows"
            $orasArch = if ([Environment]::Is64BitOperatingSystem) {
                "amd64"
            }
            else { "arm64" }
            $ext = "zip"
        }
        else {
            $orasOs = if ($IsMacOS) { "darwin" } else { "linux" }
            $arch = uname -m
            $orasArch = switch ($arch) {
                "x86_64" { "amd64" }
                "aarch64" { "arm64" }
                default { $arch }
            }
            $ext = "tar.gz"
        }
        $orasUrl = "https://github.com/oras-project/oras/releases/download" +
        "/v${orasVersion}/oras_${orasVersion}_${orasOs}_${orasArch}.${ext}"
        $archive = Join-Path $tmpDir "oras-archive.$ext"
        Invoke-WebRequest -Uri $orasUrl -OutFile $archive -UseBasicParsing
        if ($ext -eq "zip") {
            Expand-Archive -Path $archive -DestinationPath $tmpDir -Force
        }
        else {
            tar xzf $archive -C $tmpDir oras
        }
        $orasBin = Join-Path $tmpDir "oras"
        if ($IsWindows -or $env:OS -eq "Windows_NT") {
            $orasBin += ".exe"
        }
        Write-Host "Using temporary oras from $orasBin"
    }

    Write-Host "Downloading kubectl-cleanroom from $artifact ..."
    & $orasBin pull $artifact --output $tmpDir

    $tmpBinary = Join-Path $tmpDir "kubectl-cleanroom"
    if (-not (Test-Path $tmpBinary)) {
        throw "kubectl-cleanroom binary not found in artifact $artifact."
    }

    # Ensure target directory exists.
    $installDir = Split-Path -Parent $installLocation
    if ($installDir -and -not (Test-Path $installDir)) {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    }

    Move-Item -Path $tmpBinary -Destination $installLocation -Force

    if (-not ($IsWindows -or $env:OS -eq "Windows_NT")) {
        chmod +x $installLocation
    }

    Write-Host "kubectl-cleanroom installed to $installLocation"

    # Check if it's on PATH.
    if (-not (Get-Command kubectl-cleanroom -ErrorAction SilentlyContinue)) {
        Write-Warning "$installDir is not on your PATH. Add it to use kubectl-cleanroom."
    }
}
finally {
    Remove-Item -Recurse -Force $tmpDir -ErrorAction SilentlyContinue
}
