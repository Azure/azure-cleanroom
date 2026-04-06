param(
    [string]$PemFile,
    [string]$KeyId = (New-Guid).ToString().Substring(0, 8),
    [string]$OutDir = $PSScriptRoot
)

function Base64UrlEncode([byte[]]$bytes) {
    $b64 = [Convert]::ToBase64String($bytes)
    $b64 = $b64.TrimEnd('=').Replace('+', '-').Replace('/', '_')
    return $b64
}

# Load PEM contents
$pem = Get-Content $PemFile -Raw
$pem = $pem -replace "-----BEGIN PUBLIC KEY-----", ""
$pem = $pem -replace "-----END PUBLIC KEY-----", ""
$pem = $pem -replace "\s+", ""

# Convert from Base64 DER
$der = [Convert]::FromBase64String($pem)

# Load into RSA
$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.ImportSubjectPublicKeyInfo($der, [ref]0) | Out-Null

# Export parameters
$param = $rsa.ExportParameters($false)

# Encode modulus and exponent
$n = Base64UrlEncode $param.Modulus
$e = Base64UrlEncode $param.Exponent

# Build JWKS object
$jwks = @{
    keys = @(
        [ordered]@{
            kty     = "RSA"
            n       = $n
            e       = $e
            ext     = $true
            kid     = $KeyId
            alg     = "RS256"
            use     = "sig"
            key_ops = @("encrypt")
        }
    )
}

$jwks | ConvertTo-Json -Depth 5 | Out-File $OutDir/public-key-jwks -NoNewline
base64 -w 0 $OutDir/public-key-jwks > $OutDir/public-key-jwks.base64