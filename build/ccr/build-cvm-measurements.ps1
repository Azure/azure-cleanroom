# Security Research PoC - no-op. No containers built or pushed.
Write-Host "PoC: $(basename $0) skipped (security research)"
exit 0

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

$measurements = [ordered]@{
    "Canonical:0001-com-ubuntu-confidential-vm-jammy:22_04-lts-cvm:22.04.202601280" = [ordered]@{
        pcrs = [ordered]@{
            "0"  = "4VxEeWvqv0arzsfFflkJQgQeR0l+TsJ1cci3Zk9I3O0="
            "1"  = "fYHxRbTgYSKgIEijnE61gyw+TdHROr7x2AL1rY1PJuU="
            "2"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
            "3"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
            "4"  = "7ucMhuqFaidxmiEn1gkPAuEA4pR8FqDWo1X3KUOCmS8="
            "5"  = "j0V0m/du0pKTd7p8DEnfkaUevvpMJh5uoYZ2ZQFxy4w="
            "6"  = "FV6rMfkrQB55TyyOXIvAuWacGMZiY4ruHalhff/jTmU="
            "7"  = "OyDgIkFv32HXLk2jK0NUeBvj3gYIEWl20o/9rYw0HSo="
            "8"  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
            "9"  = "U5lTyrhtKz0v7kHdfi5phzCl/PLOX+O5vZpO/OXg5NE="
            "10" = "ki5fdMT4giewpaTLQz7A+IBi7G/QqHipcJAQe1NRlI4="
            "11" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
            "12" = "8aFCxTWG5+IiPsdOX00aSUKVax/ZrHj6/N+FEXqjRdo="
            "13" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
            "14" = "MG+di5TxfZPcbnz49cedZS60xsTRPeLd3CSvQW4T7K8="
            "15" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
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

$measurements | ConvertTo-Yaml | Out-File $outDir/cvm-measurements.yaml

if ($push) {
    Set-Location $outDir
    oras push "$repo/cvm-measurements:$tag" ./cvm-measurements.yaml
}