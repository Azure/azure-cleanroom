param(
    [parameter(Mandatory = $true)]
    [string]$tag,

    [parameter(Mandatory = $true)]
    [string]$repo,

    [string]$outDir = "",

    [switch]$push
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = git rev-parse --show-toplevel
$buildRoot = "$root/build"

. $buildRoot/helpers.ps1

if ($outDir -eq "") {
    $outDir = "."
}

# PCR values for insecure-virtual environment. These are sourced from
# samples/reports/cvm/insecure-virtual/encryption/attestation.json and
# are used when running with infraType=virtual (Kind cluster, no real
# CVM hardware attestation).
$measurements = [ordered]@{
    "insecure-virtual" = [ordered]@{
        cpu = [ordered]@{
            pcrs = [ordered]@{
                "0"  = "hCdbL0MSzU/Gy+axUq08NoPlE9nx4jw0/KFgyMynpqc="
                "1"  = "uRQ/dq/+1VXYYq/PBqqaaCrGia8hy4DpC4VP2oO4bRU="
                "2"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "3"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "4"  = "q2E0ay/lOjzmOOyxhvVYDbv3vVIp0mGAeumf2LzoGU4="
                "5"  = "+WofNrqDfYPeyQGWK1OPYDrIVV2f/ODnMqkhdR78eek="
                "6"  = "7Rr81The/3g8EMsrhaGvcsaYFuV4AUTkmy2snYxWYbk="
                "7"  = "OyDgIkFv32HXLk2jK0NUeBvj3gYIEWl20o/9rYw0HSo="
                "8"  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "9"  = "mAKnGa8k7WMM0xrJIAPtZgP1Jvpk8FPWfhwpFAwwtYw="
                "10" = "b5wX6fE00SZB1keqMeoKDFJBBZgLReXifq+NrkN320Q="
                "11" = "w0KBDzvLb2D1gZ8G3giyTzx6OHWhjjJQfjJSmDazLak="
                "12" = "8aFCxTWG5+IiPsdOX00aSUKVax/ZrHj6/N+FEXqjRdo="
                "13" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "14" = "MG+di5TxfZPcbnz49cedZS60xsTRPeLd3CSvQW4T7K8="
                "15" = "Y1XE1bTaZw4shlqIWzuzsKSksy4HUn9Xk5BJHbFXhsA="
                "16" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "17" = "//////////////////////////////////////////8="
                "18" = "//////////////////////////////////////////8="
                "19" = "//////////////////////////////////////////8="
                "20" = "//////////////////////////////////////////8="
                "21" = "//////////////////////////////////////////8="
                "22" = "//////////////////////////////////////////8="
                "23" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
            }
        }
    }
}

$measurements | ConvertTo-Yaml | Out-File $outDir/cvm-measurements.yaml

if ($push) {
    Set-Location $outDir
    oras push "$repo/cvm-measurements-virtual:$tag" ./cvm-measurements.yaml
}
