[CmdletBinding()]
param
(
    [string]
    $resourceGroup = "gsinhadev",

    [switch]
    $forceDeploy,

    [switch]
    $useCcePolicy2
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$ccePolicy = $useCcePolicy2 ?
"cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6IHRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGxvd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnVlLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWluZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CmdldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHsiYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQgOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CgoK"
:
"cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6IHRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGxvd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnVlLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWluZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CmdldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHsiYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQgOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cg=="

$root = git rev-parse --show-toplevel
$cgName = "attestation-report-generator"
$script:create = $true
& {
    # Check if container group already exists by doing az container show and checking its return code.
    $PSNativeCommandUseErrorActionPreference = $false
    az container show --name $cgName --resource-group $resourceGroup 1>$null 2>$null
    if ($LASTEXITCODE -eq 0) {
        $script:create = $false
    }
}

if ($script:create -or $forceDeploy) {
    Write-Host "Deleting any existing container group $cgName."
    az container delete --name $cgName --resource-group $resourceGroup -y 1>$null 2>$null
    Write-Host "Deploying container group $cgName."
    az deployment group create --resource-group $resourceGroup --template-file $root/src/tools/attestation-report-generator/deploy.bicep --parameters ccePolicy=$ccePolicy
}
else {
    Write-Host "Container group $cgName already exists in resource group $resourceGroup."
}

$timeout = New-TimeSpan -Minutes 5
$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
while ($true) {
    $generatorIP = az container show `
        --name $cgName `
        -g $resourceGroup `
        --query "ipAddress.ip" `
        --output tsv
    if ($stopwatch.elapsed -gt $timeout) {
        throw "Hit timeout waiting for IP address to be available."
    }

    if ($null -ne $generatorIP) {
        break
    }

    Write-Host "Sleeping for 5 seconds for IP address to be available."
    Start-Sleep -Seconds 5
}

Write-Host "attestation-report-generator IP address: $generatorIP"

$insecureDir = $useCcePolicy2 ? "insecure-virtual-2" : "insecure-virtual"
rm -rf $root/samples/reports/$insecureDir
mkdir -p $root/samples/reports/$insecureDir/encryption

$encryptionKeyAttestation = (curl -sS --fail-with-body -X POST http://${generatorIP}:9300/generate/rsa | ConvertFrom-Json)
Write-Host "Updating encryption key and report in samples/reports/$insecureDir."
$encryptionKeyAttestation | ConvertTo-Json -Depth 100 | Out-File "$root/samples/reports/$insecureDir/encryption/attestation.json" -NoNewline
@"
ccepolicy: $ccePolicy
hostdata: $($ccePolicy | base64 -d | sha256sum | cut -d ' ' -f 1)
"@ | Out-File "$root/samples/reports/$insecureDir/values.txt" -NoNewline

$encryptionKeyAttestation.publicKey -replace "\\n", "`n" | Out-File "$root/samples/reports/$insecureDir/encryption/pub_key.pem" -NoNewline
$encryptionKeyAttestation.privateKey -replace "\\n", "`n" | Out-File "$root/samples/reports/$insecureDir/encryption/priv_key.pem" -NoNewline

if ($useCcePolicy2) {
    exit 0 # CCE Policy 2 has a limited use case so not proceeding with remaining steps.
}

mkdir -p $root/samples/reports/$insecureDir/signing
$signingKeyAttestation = (curl -sS --fail-with-body -X POST http://${generatorIP}:9300/generate/ecdsa | ConvertFrom-Json)
Write-Host "Updating signing key and report in samples/reports/$insecureDir."
$signingKeyAttestation | ConvertTo-Json -Depth 100 | Out-File "$root/samples/reports/$insecureDir/signing/attestation.json" -NoNewline
$signingKeyAttestation.publicKey -replace "\\n", "`n" | Out-File "$root/samples/reports/$insecureDir/signing/pub_key.pem" -NoNewline
$signingKeyAttestation.privateKey -replace "\\n", "`n" | Out-File "$root/samples/reports/$insecureDir/signing/priv_key.pem" -NoNewline
$signingKeyAttestation.certificate -replace "\\n", "`n" | Out-File "$root/samples/reports/$insecureDir/signing/cert.pem" -NoNewline

Write-Host "Generating an MAA request json using the encryption public key in in samples/reports/$insecureDir as runtimeData."
pwsh $PSScriptRoot/PemToJwks.ps1 -PemFile $root/samples/reports/$insecureDir/encryption/pub_key.pem -OutDir $root/samples/reports/$insecureDir/encryption

curl -sS --fail-with-body -X POST http://${generatorIP}:9300/generate/maa_request -H "Content-Type: application/json" -d `
    @"
{
        "runtimeData": "$(Get-Content $root/samples/reports/$insecureDir/encryption/public-key-jwks.base64)"
}
"@

Write-Host "Extracting MAA request json for encryption public key in samples/reports/$insecureDir."
$maaRequestJson = (curl -sS --fail-with-body -X POST http://${generatorIP}:9300/extract/maa_request | ConvertFrom-Json)
$maaRequestJson | ConvertTo-Json -Depth 100 | Out-File "$root/samples/reports/$insecureDir/encryption/maa-request.json" -NoNewline
