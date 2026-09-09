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
                "1"  = "F7yW/nkHLKzBadspX6G2C8dWUfqmpd/YKvPFQejQK8M="
                "2"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "3"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "4"  = "Tj919q1Q/Rgz1959ZZpF+H09iMix5qazS0zRuagXWss="
                "5"  = "nzZvUgB1C/6E8TH961WEdZSl8dQwlRPq9HbKJ9f+zDw="
                "6"  = "FxW1lK+mXKTMYHyRXlKWNLFy4PugiGUtEtLvgukD5kE="
                "7"  = "8BTly/opfueHqXarxRvB1n4j4bnkpgEooRBt0K3vDFs="
                "8"  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "9"  = "ZQ70W+2+FQ5U3+To8c3HHjqgYKlkqx2oRu1Pl6URfQI="
                "10" = "X5wMZ2/+OEsgRBF37XdU4Cy53/Ow9Lp3Tmu49XPx6Y8="
                "11" = "ikWrUbD2LMjLR0oK6yRoTVqc9D05ggIJ62CB0IQToKQ="
                "12" = "8aFCxTWG5+IiPsdOX00aSUKVax/ZrHj6/N+FEXqjRdo="
                "13" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "14" = "MG+di5TxfZPcbnz49cedZS60xsTRPeLd3CSvQW4T7K8="
                "15" = "5xTTWW3XBoZ0KGp/yqx2icKoBmgew/N3WkEH/RAIxyo="
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
