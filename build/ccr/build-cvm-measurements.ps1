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

# PCR values are grouped by VM SKU (cpu and gpu) under each image.
# cpu: Standard_DC2as_v5 (no GPU).
# gpu: Standard_NCC40ads_H100_v5 (with nvidia-driver-580-server-open).
#
# To derive PCR values for a new image version:
#
# cpu:
#   ./src/cvm/tests/deploy-cvm.ps1
#   SSH in and read: cat /sys/class/tpm/tpm0/pcr-sha256/{0..23}
#   Convert: echo <hex> | xxd -r -p | base64
#
# gpu:
#   ./src/cvm/tests/deploy-cvm.ps1 -Gpu
#   SSH in and read: cat /sys/class/tpm/tpm0/pcr-sha256/{0..23}
#   Convert: echo <hex> | xxd -r -p | base64
#
$measurements = [ordered]@{
    "Canonical:ubuntu-24_04-lts:cvm:24.04.202604160" = [ordered]@{
        cpu = [ordered]@{
            pcrs = [ordered]@{
                "0"  = "hCdbL0MSzU/Gy+axUq08NoPlE9nx4jw0/KFgyMynpqc="
                "1"  = "uRQ/dq/+1VXYYq/PBqqaaCrGia8hy4DpC4VP2oO4bRU="
                "2"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "3"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "4"  = "q2E0ay/lOjzmOOyxhvVYDbv3vVIp0mGAeumf2LzoGU4="
                "5"  = "+WofNrqDfYPeyQGWK1OPYDrIVV2f/ODnMqkhdR78eek="
                "6"  = "O8FVMbX1SZIc6iw45VQUaraq+6ZgwZ7ek/PC8IFkzds="
                "7"  = "OyDgIkFv32HXLk2jK0NUeBvj3gYIEWl20o/9rYw0HSo="
                "8"  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "9"  = "mAKnGa8k7WMM0xrJIAPtZgP1Jvpk8FPWfhwpFAwwtYw="
                "10" = "2fR1RhY3vJedXotcvgg9jvuvaMQ2w+oux9EN4LQF8JM="
                "11" = "w0KBDzvLb2D1gZ8G3giyTzx6OHWhjjJQfjJSmDazLak="
                "12" = "8aFCxTWG5+IiPsdOX00aSUKVax/ZrHj6/N+FEXqjRdo="
                "13" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "14" = "MG+di5TxfZPcbnz49cedZS60xsTRPeLd3CSvQW4T7K8="
                "15" = "016WIdy91OF6lUF7dZwhUhyFdsGrPl//sZwTrwPsrbo="
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
        gpu = [ordered]@{
            pcrs = [ordered]@{
                "0"  = "Exd6ZTW63xlBXAZYlwXdWhiQ9zVFxKn+96z+LGF3orc="
                "1"  = "uRQ/dq/+1VXYYq/PBqqaaCrGia8hy4DpC4VP2oO4bRU="
                "2"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "3"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "4"  = "q2E0ay/lOjzmOOyxhvVYDbv3vVIp0mGAeumf2LzoGU4="
                "5"  = "+WofNrqDfYPeyQGWK1OPYDrIVV2f/ODnMqkhdR78eek="
                "6"  = "+uc42JzBKEU/pyZ82vSGCLQqir6inje+v+Uw9cOtGW4="
                "7"  = "OyDgIkFv32HXLk2jK0NUeBvj3gYIEWl20o/9rYw0HSo="
                "8"  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "9"  = "mAKnGa8k7WMM0xrJIAPtZgP1Jvpk8FPWfhwpFAwwtYw="
                "10" = "DkVmqU1IavanyuNV3bKr9LMinhHe6VUxcPEJVYeo930="
                "11" = "w0KBDzvLb2D1gZ8G3giyTzx6OHWhjjJQfjJSmDazLak="
                "12" = "8aFCxTWG5+IiPsdOX00aSUKVax/ZrHj6/N+FEXqjRdo="
                "13" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "14" = "MG+di5TxfZPcbnz49cedZS60xsTRPeLd3CSvQW4T7K8="
                "15" = "G2AtObmc2a0urVTTFfwJaVVNX9T/eERSkgbb4/zYheo="
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
    # image-prep: custom published flex node image.
    "cleanroom-flexnode-image:0.1.0"                 = [ordered]@{
        cpu = [ordered]@{
            pcrs = [ordered]@{
                "0"  = "hCdbL0MSzU/Gy+axUq08NoPlE9nx4jw0/KFgyMynpqc="
                "1"  = "uRQ/dq/+1VXYYq/PBqqaaCrGia8hy4DpC4VP2oO4bRU="
                "2"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "3"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "4"  = "dSCHLRiwy+NgRszsPYmfa1qJNkrzffAexnerjx+4qUQ="
                "5"  = "+WofNrqDfYPeyQGWK1OPYDrIVV2f/ODnMqkhdR78eek="
                "6"  = "O8FVMbX1SZIc6iw45VQUaraq+6ZgwZ7ek/PC8IFkzds="
                "7"  = "OyDgIkFv32HXLk2jK0NUeBvj3gYIEWl20o/9rYw0HSo="
                "8"  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "9"  = "mAKnGa8k7WMM0xrJIAPtZgP1Jvpk8FPWfhwpFAwwtYw="
                "10" = "2fR1RhY3vJedXotcvgg9jvuvaMQ2w+oux9EN4LQF8JM="
                "11" = "w0KBDzvLb2D1gZ8G3giyTzx6OHWhjjJQfjJSmDazLak="
                "12" = "8aFCxTWG5+IiPsdOX00aSUKVax/ZrHj6/N+FEXqjRdo="
                "13" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "14" = "MG+di5TxfZPcbnz49cedZS60xsTRPeLd3CSvQW4T7K8="
                "15" = "016WIdy91OF6lUF7dZwhUhyFdsGrPl//sZwTrwPsrbo="
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
        gpu = [ordered]@{
            pcrs = [ordered]@{
                "0"  = "Exd6ZTW63xlBXAZYlwXdWhiQ9zVFxKn+96z+LGF3orc="
                "1"  = "uRQ/dq/+1VXYYq/PBqqaaCrGia8hy4DpC4VP2oO4bRU="
                "2"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "3"  = "PUWM/lXMA+ofRD8VYr7sjfUcdeFKn8+acjShPxmOeWk="
                "4"  = "q2E0ay/lOjzmOOyxhvVYDbv3vVIp0mGAeumf2LzoGU4="
                "5"  = "+WofNrqDfYPeyQGWK1OPYDrIVV2f/ODnMqkhdR78eek="
                "6"  = "+uc42JzBKEU/pyZ82vSGCLQqir6inje+v+Uw9cOtGW4="
                "7"  = "OyDgIkFv32HXLk2jK0NUeBvj3gYIEWl20o/9rYw0HSo="
                "8"  = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "9"  = "mAKnGa8k7WMM0xrJIAPtZgP1Jvpk8FPWfhwpFAwwtYw="
                "10" = "DkVmqU1IavanyuNV3bKr9LMinhHe6VUxcPEJVYeo930="
                "11" = "w0KBDzvLb2D1gZ8G3giyTzx6OHWhjjJQfjJSmDazLak="
                "12" = "8aFCxTWG5+IiPsdOX00aSUKVax/ZrHj6/N+FEXqjRdo="
                "13" = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                "14" = "MG+di5TxfZPcbnz49cedZS60xsTRPeLd3CSvQW4T7K8="
                "15" = "G2AtObmc2a0urVTTFfwJaVVNX9T/eERSkgbb4/zYheo="
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
    oras push "$repo/cvm-measurements:$tag" ./cvm-measurements.yaml
}