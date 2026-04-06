# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Generate CVM attestation reports (encryption + signing keys) on an Azure
    Confidential VM and save them under samples/reports/cvm/insecure-virtual.
.DESCRIPTION
    1. Deploys an Azure CVM via src/cvm/tests/deploy-cvm.ps1.
    2. Builds cvm-attestation-agent and attestation-report-generator Docker images locally.
    3. Copies both image tarballs to the CVM.
    4. Copies a docker-compose file to the CVM and starts both containers.
    5. Invokes /generate/rsa and /generate/ecdsa on the attestation-report-generator.
    6. Downloads the results and writes them to samples/reports/cvm/insecure-virtual/.
.EXAMPLE
    ./samples/reports/generate-cvm-reports.ps1
.EXAMPLE
    ./samples/reports/generate-cvm-reports.ps1 -Location eastus2 -skipDeploy
#>
[CmdletBinding()]
param(
    [string]$Location = "westeurope",

    [switch]$skipDeploy
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$cvmTestsDir = "$root/src/cvm/tests"
$generatedDir = Join-Path $PSScriptRoot "generated"
$testTag = "test"

# Image names (no registry prefix for local builds).
$agentImageName = "cvm/cvm-attestation-agent"
$reportGenImageName = "attestation-report-generator"

# Container / compose names.
$agentContainerName = "cvm-attestation-agent"
$reportGenContainerName = "attestation-report-generator"
$reportGenPort = "9300"

# ──────────────────────────────────────────────────────────────────────────────
# 1. Deploy CVM (reuses deploy-cvm.ps1 which writes generated/cvm-deploy.json).
# ──────────────────────────────────────────────────────────────────────────────
if (-not $skipDeploy) {
    Write-Host "=== Deploying Azure CVM ==="
    & pwsh "$cvmTestsDir/deploy-cvm.ps1" -Location $Location -OutDir $generatedDir
    Write-Host ""
}

# --- Read deployment info ---
$deployJson = Join-Path $generatedDir "cvm-deploy.json"
if (-not (Test-Path $deployJson)) {
    throw "$deployJson not found. Run deploy-cvm.ps1 first."
}

$deploy = Get-Content $deployJson | ConvertFrom-Json
$vmHost = $deploy.vm_host
$sshKey = $deploy.ssh_key
$sshOpts = "-i $sshKey -o StrictHostKeyChecking=no -o ConnectTimeout=10"

# --- Helper to run SSH commands ---
function Invoke-SshScript {
    param([string]$Script)
    bash -c "ssh $sshOpts $vmHost bash -s <<'REMOTEOF'
$Script
REMOTEOF"
}

Write-Host "=== Generate CVM Attestation Reports ==="
Write-Host "Target: $vmHost"
Write-Host ""

# ──────────────────────────────────────────────────────────────────────────────
# 2. Build Docker images locally.
# ──────────────────────────────────────────────────────────────────────────────
Write-Host "--- Building cvm-attestation-agent Docker image ---"
& pwsh "$root/build/cvm/build-cvm-attestation-agent.ps1" -tag $testTag -repo ""
Write-Host ""

Write-Host "--- Building attestation-report-generator Docker image ---"
& pwsh "$root/build/ccr/build-attestation-report-generator.ps1" -tag $testTag -repo ""
Write-Host ""

# ──────────────────────────────────────────────────────────────────────────────
# 3. Export images as tarballs and upload to the CVM.
# ──────────────────────────────────────────────────────────────────────────────
$agentTar = Join-Path $generatedDir "cvm-attestation-agent-image.tar.gz"
$reportGenTar = Join-Path $generatedDir "attestation-report-generator-image.tar.gz"

Write-Host "--- Exporting Docker images ---"
docker save "${agentImageName}:${testTag}" | gzip > $agentTar
$tarSize = (Get-Item $agentTar).Length / 1MB
Write-Host ("  cvm-attestation-agent ({0:N1} MB)" -f $tarSize)

docker save "${reportGenImageName}:${testTag}" | gzip > $reportGenTar
$tarSize = (Get-Item $reportGenTar).Length / 1MB
Write-Host ("  attestation-report-generator ({0:N1} MB)" -f $tarSize)
Write-Host ""

Write-Host "--- Uploading Docker images to CVM ---"
bash -c "scp $sshOpts '$agentTar' '${vmHost}:/tmp/cvm-attestation-agent-image.tar.gz'"
bash -c "scp $sshOpts '$reportGenTar' '${vmHost}:/tmp/attestation-report-generator-image.tar.gz'"
Write-Host ""

# ──────────────────────────────────────────────────────────────────────────────
# 4. Upload docker-compose file, install Docker, load images, start containers,
#    invoke /generate/rsa and /generate/ecdsa, save output.
# ──────────────────────────────────────────────────────────────────────────────
$composeFile = "$root/samples/reports/docker-compose-cvm-reports.yaml"
Write-Host "--- Uploading docker-compose file to CVM ---"
bash -c "scp $sshOpts '$composeFile' '${vmHost}:/tmp/docker-compose-reports.yaml'"
Write-Host ""

# The inline script uses a single-quoted heredoc ('REMOTEOF') so no escaping is needed.
Write-Host "--- Setting up and running containers on CVM ---"
Invoke-SshScript @'
set -euo pipefail

# ── Install Docker if not present ──
if ! command -v docker &>/dev/null; then
    echo "Installing Docker..."
    curl -fsSL https://get.docker.com | sudo sh
    sudo usermod -aG docker $USER
fi

# ── Clean up any previous containers ──
sudo docker rm -f cvm-attestation-agent 2>/dev/null || true
sudo docker rm -f attestation-report-generator 2>/dev/null || true

# ── Load images ──
echo "Loading Docker images..."
sudo docker load < /tmp/cvm-attestation-agent-image.tar.gz
sudo docker load < /tmp/attestation-report-generator-image.tar.gz
echo "Docker images loaded"

echo "Starting containers via docker compose..."
sudo docker compose -f /tmp/docker-compose-reports.yaml up -d

# ── Wait for attestation-report-generator to be ready ──
echo "Waiting for attestation-report-generator to be ready..."
PORT=9300
for i in $(seq 1 30); do
    if curl -s --connect-timeout 2 -o /dev/null http://localhost:${PORT}/ 2>/dev/null; then
        echo "attestation-report-generator is ready"
        break
    fi
    if [ "$i" -eq "30" ]; then
        echo "ERROR: attestation-report-generator not ready after 30 attempts"
        echo "=== cvm-attestation-agent logs ==="
        sudo docker logs cvm-attestation-agent 2>&1
        echo "=== attestation-report-generator logs ==="
        sudo docker logs attestation-report-generator 2>&1
        exit 1
    fi
    sleep 2
done

# ── Call /generate/rsa ──
echo "Calling POST /generate/rsa ..."
HTTP_CODE=$(curl -s -w '%{http_code}' -o /tmp/rsa_attestation.json \
    -X POST "http://localhost:${PORT}/generate/rsa" \
    -H 'Content-Type: application/json')
echo "  HTTP status: ${HTTP_CODE}"
if [[ "${HTTP_CODE}" != "200" ]]; then
    echo "ERROR: /generate/rsa failed"
    cat /tmp/rsa_attestation.json 2>/dev/null || true
    exit 1
fi
echo "  RSA attestation saved to /tmp/rsa_attestation.json"

# ── Call /generate/ecdsa ──
echo "Calling POST /generate/ecdsa ..."
HTTP_CODE=$(curl -s -w '%{http_code}' -o /tmp/ecdsa_attestation.json \
    -X POST "http://localhost:${PORT}/generate/ecdsa" \
    -H 'Content-Type: application/json')
echo "  HTTP status: ${HTTP_CODE}"
if [[ "${HTTP_CODE}" != "200" ]]; then
    echo "ERROR: /generate/ecdsa failed"
    cat /tmp/ecdsa_attestation.json 2>/dev/null || true
    exit 1
fi
echo "  ECDSA attestation saved to /tmp/ecdsa_attestation.json"

# ── Print container logs for debugging ──
echo ""
echo "=== cvm-attestation-agent logs ==="
sudo docker logs cvm-attestation-agent 2>&1 | tail -20
echo "=== attestation-report-generator logs ==="
sudo docker logs attestation-report-generator 2>&1 | tail -20

# ── Stop containers ──
sudo docker compose -f /tmp/docker-compose-reports.yaml down
echo "Containers stopped"
'@
Write-Host ""

# ──────────────────────────────────────────────────────────────────────────────
# 5. Download results from CVM.
# ──────────────────────────────────────────────────────────────────────────────
Write-Host "--- Downloading attestation results ---"
$localOut = Join-Path $generatedDir "reports"
New-Item -ItemType Directory -Path $localOut -Force | Out-Null

$rsaFile = Join-Path $localOut "rsa_attestation.json"
$ecdsaFile = Join-Path $localOut "ecdsa_attestation.json"
bash -c "scp $sshOpts '${vmHost}:/tmp/rsa_attestation.json' '$rsaFile'"
bash -c "scp $sshOpts '${vmHost}:/tmp/ecdsa_attestation.json' '$ecdsaFile'"
Write-Host "  RSA:   $rsaFile"
Write-Host "  ECDSA: $ecdsaFile"
Write-Host ""

# ──────────────────────────────────────────────────────────────────────────────
# 6. Write results to samples/reports/cvm/insecure-virtual/.
# ──────────────────────────────────────────────────────────────────────────────
Write-Host "--- Writing reports to samples/reports/cvm/insecure-virtual ---"
$outDir = "$root/samples/reports/cvm/insecure-virtual"
Remove-Item -Recurse -Force $outDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$outDir/encryption" -Force | Out-Null
New-Item -ItemType Directory -Path "$outDir/signing" -Force | Out-Null

# -- Encryption (RSA) --
$rsaAttestation = Get-Content $rsaFile | ConvertFrom-Json
$rsaAttestation | ConvertTo-Json -Depth 100 |
Out-File "$outDir/encryption/attestation.json" -NoNewline

$rsaAttestation.publicKey -replace "\\n", "`n" |
Out-File "$outDir/encryption/pub_key.pem" -NoNewline

$rsaAttestation.privateKey -replace "\\n", "`n" |
Out-File "$outDir/encryption/priv_key.pem" -NoNewline

Write-Host "  encryption/attestation.json"
Write-Host "  encryption/pub_key.pem"
Write-Host "  encryption/priv_key.pem"

# -- Signing (ECDSA) --
$ecdsaAttestation = Get-Content $ecdsaFile | ConvertFrom-Json
$ecdsaAttestation | ConvertTo-Json -Depth 100 |
Out-File "$outDir/signing/attestation.json" -NoNewline

$ecdsaAttestation.publicKey -replace "\\n", "`n" |
Out-File "$outDir/signing/pub_key.pem" -NoNewline

$ecdsaAttestation.privateKey -replace "\\n", "`n" |
Out-File "$outDir/signing/priv_key.pem" -NoNewline

$ecdsaAttestation.certificate -replace "\\n", "`n" |
Out-File "$outDir/signing/cert.pem" -NoNewline

Write-Host "  signing/attestation.json"
Write-Host "  signing/pub_key.pem"
Write-Host "  signing/priv_key.pem"
Write-Host "  signing/cert.pem"
Write-Host ""

Write-Host "Done. Reports written to $outDir"
