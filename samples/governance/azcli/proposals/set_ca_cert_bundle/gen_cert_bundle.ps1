#https://learn.microsoft.com/en-us/powershell/scripting/learn/experimental-features?view=powershell-7.4#psnativecommanderroractionpreference
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

Import-Module $PSScriptRoot/../../../common/common.psm1

$certs = (
    "https://cacerts.digicert.com/DigiCertGlobalRootCA.crt",
    "https://cacerts.digicert.com/DigiCertGlobalRootG2.crt",
    "https://cacerts.digicert.com/DigiCertGlobalRootG3.crt",
    "https://web.entrust.com/root-certificates/entrust_g2_ca.cer",
    "https://www.microsoft.com/pkiops/certs/Microsoft%20ECC%20Root%20Certificate%20Authority%202017.crt",
    "https://www.microsoft.com/pkiops/certs/Microsoft%20RSA%20Root%20Certificate%20Authority%202017.crt"
)

$certDir = "$PSScriptRoot/certs"
foreach ($cert in $certs) {
    $crtFile = "$certDir/$($cert.Split('/')[-1])"
    Write-Log Verbose `
        "Downloading cert from '$cert'..."
    curl -s -L $cert -o $crtFile
    if ($crtFile.EndsWith(".cer")) {
        $pemString = (Get-Content $crtFile -Raw).TrimEnd("`r`n")
    }
    else {
        $derBytes = [System.IO.File]::ReadAllBytes($crtFile)
        $base64Cert = [System.Convert]::ToBase64String($derBytes, 'InsertLineBreaks')
        # Wrap in PEM markers
        $pemString = @"
-----BEGIN CERTIFICATE-----
$base64Cert
-----END CERTIFICATE-----
"@
    }

    # Output to console
    $pemString = $pemString.ReplaceLineEndings("\n")
    $combinedPem += $pemString + "\n"
}

$combinedPem | Out-File $certDir/cert_bundle.pem

