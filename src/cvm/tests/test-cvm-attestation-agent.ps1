# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
    Test script for cvm-attestation-agent running as a Docker container on Azure CVM.
.DESCRIPTION
    Builds the Docker image locally, exports it as a tarball, copies it to the CVM,
    loads it, runs the container with TPM device access, calls POST /snp/attest,
    validates the runtime claims user-data, then runs the cvm-attestation-verifier
    locally and verifies the collected evidence passes all checks.

    VM host and SSH key are read from generated/cvm-deploy.json produced by
    deploy-cvm.ps1.
.EXAMPLE
    ./src/cvm/tests/test-cvm-attestation-agent.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

# --- Configuration ---
$testTag = "test"
$adminUser = "azureuser"
$serverImageName = "cvm/cvm-attestation-agent"
$serverContainerName = "cvm-attestation-agent-test"
$serverPort = "8900"
$verifierImageName = "cvm/cvm-attestation-verifier"
$verifierContainerName = "cvm-attestation-verifier-test"
$verifierPort = "8902"

$generatedDir = Join-Path $PSScriptRoot "generated"
$localOut = Join-Path $generatedDir "attest"
$rootDir = (Resolve-Path (Join-Path $PSScriptRoot "../../..")).Path
$serverImageTar = Join-Path $generatedDir "cvm-attestation-agent-image.tar.gz"

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
function Invoke-Ssh {
    param([string]$Command)
    bash -c "ssh $sshOpts $vmHost '$Command'"
}

function Invoke-SshScript {
    param([string]$Script)
    bash -c "ssh $sshOpts $vmHost bash -s <<'REMOTEOF'
$Script
REMOTEOF"
}

# 0. Clean up previous run artifacts.
Write-Host "--- Cleaning up previous run artifacts ---"
if (Test-Path $localOut) { Remove-Item -Recurse -Force $localOut }
if (Test-Path $serverImageTar) { Remove-Item -Force $serverImageTar }
docker rm -f $serverContainerName 2>$null
docker rm -f $verifierContainerName 2>$null
Write-Host "  Done"
Write-Host ""

Write-Host "=== CVM Attestation Agent Container Test ==="
Write-Host "Target: $vmHost"
Write-Host ""

# 1. Build Docker images.
Write-Host "--- Building cvm-attestation-agent Docker image ---"
& pwsh "$rootDir/build/cvm/build-cvm-attestation-agent.ps1" -tag $testTag -repo ""
Write-Host ""

Write-Host "--- Building cvm-attestation-verifier Docker image ---"
& pwsh "$rootDir/build/cvm/build-cvm-attestation-verifier.ps1" -tag $testTag -repo ""
Write-Host ""

# 2. Export image as tarball.
Write-Host "--- Exporting Docker image ---"
New-Item -ItemType Directory -Path $generatedDir -Force | Out-Null
docker save "${serverImageName}:${testTag}" | gzip > $serverImageTar
$tarSize = (Get-Item $serverImageTar).Length / 1MB
Write-Host ("  Image saved to $serverImageTar ({0:N1} MB)" -f $tarSize)
Write-Host ""

# 3. Generate RSA key pair and compute report_data = SHA256(pubkey PEM UTF-8) || zeros.
#    This matches how GenerateRsaKeyPairAndReportAsync in Attestation.cs constructs
#    report_data: Encoding.UTF8.GetBytes(publicKeyPem) -> SHA256.
Write-Host "--- Generating RSA key pair ---"
New-Item -ItemType Directory -Path $localOut -Force | Out-Null
$rsaPrivate = Join-Path $localOut "priv_key.pem"
$rsaPublicPem = Join-Path $localOut "pub_key.pem"

openssl genrsa -out $rsaPrivate 2048 2>$null
openssl rsa -in $rsaPrivate -pubout -outform PEM -out $rsaPublicPem 2>$null
Write-Host "  Private key: $rsaPrivate"
Write-Host "  Public key (PEM): $rsaPublicPem"

# report_data = SHA256(pubkey PEM UTF-8 bytes) (32 bytes) + 32 zero bytes = 64 bytes total.
$pubkeyHash = bash -c "cat '$rsaPublicPem' | tr -d '\r' | sha256sum" | ForEach-Object { $_.Split(' ')[0] }
Write-Host "  SHA256(pubkey PEM): $pubkeyHash"

$reportDataHex = "${pubkeyHash}$("0" * 64)"
$reportDataB64 = bash -c "printf '%s' '$reportDataHex' | xxd -r -p | base64 -w0"
Write-Host "  report_data (base64): $reportDataB64"

$nonceB64 = openssl rand -base64 32
Write-Host "  nonce (base64): $nonceB64"
Write-Host ""

# 4. Upload image tarball to CVM.
Write-Host "--- Uploading Docker image to CVM ---"
bash -c "scp $sshOpts '$serverImageTar' '${vmHost}:/tmp/cvm-attestation-agent-image.tar.gz'"
Write-Host ""

# 5. Install Docker on CVM if needed, load image.
Write-Host "--- Setting up container on CVM ---"
Invoke-SshScript @"
set -euo pipefail
if ! command -v docker &>/dev/null; then
    echo "  Installing Docker..."
    curl -fsSL https://get.docker.com | sudo sh
    sudo usermod -aG docker `$USER
fi
sudo docker rm -f $serverContainerName 2>/dev/null || true
echo "  Loading Docker image..."
sudo docker load < /tmp/cvm-attestation-agent-image.tar.gz
echo "  Docker image loaded"
"@
Write-Host ""

# 6. Write request JSON and upload to CVM.
Write-Host "--- Preparing request ---"
$requestJsonFile = Join-Path $localOut "attest_request.json"
@{
    reportData = $reportDataB64
    nonce      = $nonceB64
} | ConvertTo-Json | Set-Content $requestJsonFile
bash -c "scp $sshOpts '$requestJsonFile' '${vmHost}:/tmp/attest_request.json'"
Write-Host "  Request JSON uploaded to CVM:/tmp/attest_request.json"
Write-Host ""

# 7. Run container with TPM access, call API, collect response.
Write-Host "--- Running attestation container on CVM ---"
$remoteScript = @"
set -euo pipefail
echo "  Starting container..."
sudo docker run -d \
    --name $serverContainerName \
    --device /dev/tpmrm0:/dev/tpmrm0 \
    -p ${serverPort}:${serverPort} \
    ${serverImageName}:${testTag}

echo "  Waiting for server to start..."
for i in `$(seq 1 20); do
    if curl -sf -o /dev/null http://localhost:${serverPort}/snp/attest -X POST -d '{}' 2>/dev/null; then
        break
    fi
    if ! sudo docker ps -q -f name=$serverContainerName | grep -q .; then
        echo "  ERROR: Container exited unexpectedly. Logs:"
        sudo docker logs $serverContainerName 2>&1
        exit 1
    fi
    sleep 0.5
done

if ! sudo docker ps -q -f name=$serverContainerName | grep -q .; then
    echo "  ERROR: Container not running. Logs:"
    sudo docker logs $serverContainerName 2>&1
    exit 1
fi
echo "  Container is running"

echo "  Calling POST /snp/attest ..."
sudo rm -f /tmp/attest_response.json
HTTP_CODE=`$(curl -s -w '%{http_code}' -o /tmp/attest_response.json \
    -X POST "http://localhost:${serverPort}/snp/attest" \
    -H 'Content-Type: application/json' \
    -d @/tmp/attest_request.json)
echo "  HTTP status: `${HTTP_CODE}"

echo "  Container logs:"
sudo docker logs $serverContainerName 2>&1 | sed 's/^/    /'

sudo docker rm -f $serverContainerName 2>/dev/null || true
echo "  Container stopped"

if [[ "`${HTTP_CODE}" != "200" ]]; then
    echo "  ERROR: attestation request failed"
    cat /tmp/attest_response.json 2>/dev/null || true
    exit 1
fi
echo "  Response saved to CVM:/tmp/attest_response.json"
"@

Invoke-SshScript $remoteScript
Write-Host ""

# 8. Download response.
Write-Host "--- Downloading response ---"
$responseFile = Join-Path $localOut "attest_response.json"
bash -c "scp $sshOpts '${vmHost}:/tmp/attest_response.json' '$responseFile'"
Write-Host "  Response saved to $responseFile"
Write-Host ""

# 9. Extract and save individual artifacts.
Write-Host "--- Extracting artifacts ---"
$response = Get-Content $responseFile | ConvertFrom-Json
$evidence = $response.evidence

function Save-Base64Artifact {
    param([string]$FieldName, [string]$FileName)
    $value = $evidence.$FieldName
    if ($value) {
        $bytes = [Convert]::FromBase64String($value)
        [IO.File]::WriteAllBytes((Join-Path $localOut $FileName), $bytes)
        Write-Host "  $FileName ($($bytes.Length) bytes)"
    }
}

Save-Base64Artifact "tpmQuote" "tpm_quote.bin"
Save-Base64Artifact "hclReport" "hcl_report.bin"
Save-Base64Artifact "snpReport" "snp_report.bin"
Save-Base64Artifact "aikCert" "aik_cert.der"

if ($evidence.pcrs) {
    $evidence.pcrs | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $localOut "pcr_values.json")
    Write-Host "  pcr_values.json"
}
if ($evidence.runtimeClaims) {
    $evidence.runtimeClaims | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $localOut "runtime_claims.json")
    Write-Host "  runtime_claims.json"
}
Write-Host ""

# 10. Summary.
Write-Host "--- Artifacts ---"
Get-ChildItem $localOut | Format-Table Name, Length -AutoSize | Out-String | Write-Host
Write-Host ""

# 11. Validate runtime claims user-data matches public key hash.
Write-Host "--- Validating runtime claims ---"
$runtimeClaimsFile = Join-Path $localOut "runtime_claims.json"
if (Test-Path $runtimeClaimsFile) {
    $claims = Get-Content $runtimeClaimsFile | ConvertFrom-Json
    $userData = $claims."user-data"
    if (-not $userData) { $userData = $claims.userData }

    if (-not $userData) {
        Write-Host "  WARNING: user-data field not found in runtime_claims.json"
        Write-Host ""
        Write-Host "  Runtime claims contents:"
        Get-Content $runtimeClaimsFile | Write-Host
    }
    else {
        Write-Host "  user-data from runtime claims:"
        Write-Host "    $userData"

        # Expected: SHA256(pubkey PEM UTF-8) || 32 zero bytes (matching step 3).
        $expectedUserData = "${pubkeyHash}$("0" * 64)"
        Write-Host "  expected user-data (SHA256(PEM) || zeros):"
        Write-Host "    $expectedUserData"

        if ($userData.ToLower() -eq $expectedUserData.ToLower()) {
            Write-Host ""
            Write-Host "  PASS: user-data matches SHA256(public key)"
        }
        else {
            Write-Host ""
            throw "FAIL: user-data does not match expected value"
        }
    }
}
else {
    Write-Host "  WARNING: runtime_claims.json not found, skipping validation"
}
Write-Host ""

# ══════════════════════════════════════════════════════════════════════════════
# Attestation Verifier
# ══════════════════════════════════════════════════════════════════════════════

Write-Host "=== Attestation Verifier Test ==="
Write-Host ""

# 12. Run verifier container locally.
Write-Host "--- Starting cvm-attestation-verifier container ---"
docker rm -f $verifierContainerName 2>$null
docker run -d `
    --name $verifierContainerName `
    -p "${verifierPort}:8901" `
    "${verifierImageName}:${testTag}"

Write-Host "  Waiting for verifier to start..."
for ($i = 1; $i -le 20; $i++) {
    $ready = bash -c "curl -sf -o /dev/null 'http://localhost:${verifierPort}/snp/verify' -X POST -d '{}' 2>/dev/null && echo ok"
    if ($ready -eq "ok") { break }
    Start-Sleep -Milliseconds 500
}
Write-Host "  Verifier is running"
Write-Host ""

# 13. Build verify request from the saved attest response.
Write-Host "--- Building verify request ---"
$verifyRequestFile = Join-Path $localOut "verify_request.json"
$attestResponse = Get-Content $responseFile | ConvertFrom-Json
$evidenceCopy = $attestResponse.evidence | ConvertTo-Json -Depth 10 | ConvertFrom-Json
$evidenceCopy.PSObject.Properties.Remove("runtimeClaims")

@{
    evidence             = $evidenceCopy
    nonce                = $attestResponse.nonce
    platformCertificates = $attestResponse.platformCertificates
} | ConvertTo-Json -Depth 10 | Set-Content $verifyRequestFile
Write-Host "  Verify request saved to $verifyRequestFile"
Write-Host ""

# 14. Call POST /snp/verify.
Write-Host "--- Calling POST /snp/verify ---"
$verifyResponseFile = Join-Path $localOut "verify_response.json"
$httpCode = bash -c "curl -s -w '%{http_code}' -o '$verifyResponseFile' -X POST 'http://localhost:${verifierPort}/snp/verify' -H 'Content-Type: application/json' -d @'$verifyRequestFile'"
Write-Host "  HTTP status: $httpCode"

# Stop verifier container.
docker rm -f $verifierContainerName 2>$null
Write-Host "  Verifier container stopped"
Write-Host ""

if ($httpCode -ne "200") {
    $body = Get-Content $verifyResponseFile -ErrorAction SilentlyContinue
    Write-Host "  $body"
    throw "ERROR: verify request failed (HTTP $httpCode)"
}

# 15. Display verification results.
Write-Host "--- Verification Results ---"
$verifyResponse = Get-Content $verifyResponseFile | ConvertFrom-Json
$verifyResponse | ConvertTo-Json -Depth 10 | Write-Host
Write-Host ""

# 16. Check overall verdict.
if ($verifyResponse.verified -eq $true) {
    Write-Host "  PASS: All verification checks passed"
}
else {
    Write-Host "  FAIL: Verification failed"
    Write-Host ""
    Write-Host "  Failed checks:"
    $verifyResponse.checks.PSObject.Properties | Where-Object {
        $_.Value.passed -eq $false
    } | ForEach-Object {
        Write-Host "    $($_.Name): $($_.Value.error)"
    }
    throw "Verification failed"
}

Write-Host ""
Write-Host "Done."
