# Test: basic multi-contract CSI mount validation.
# Called from run-collab.ps1 in CSI mode.
# Uses two temporary pods with identical CSI volume attributes except contractId,
# and validates that both can mount and reach Running.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PrimaryContractId,

    [string]$SecondaryContractId = ""
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Log {
    param([string]$msg)
    $timestamp = Get-Date -Format "HH:mm:ss"
    Write-Host "[$timestamp] $msg"
}

function Wait-PodRunning {
    param(
        [string]$PodName,
        [int]$TimeoutSeconds = 120
    )

    $elapsed = 0
    $interval = 5
    while ($elapsed -lt $TimeoutSeconds) {
        $phase = kubectl get pod $PodName -o jsonpath='{.status.phase}' 2>$null
        if ($phase -eq 'Running') {
            return $true
        }

        $failedMount = kubectl get events `
            --field-selector involvedObject.name=$PodName,reason=FailedMount `
            -o jsonpath='{.items[*].message}' 2>$null
        if ($failedMount) {
            throw "Pod $PodName failed to mount: $failedMount"
        }

        Start-Sleep -Seconds $interval
        $elapsed += $interval
    }

    $phase = kubectl get pod $PodName -o jsonpath='{.status.phase}' 2>$null
    $failedMount = kubectl get events `
        --field-selector involvedObject.name=$PodName,reason=FailedMount `
        -o jsonpath='{.items[*].message}' 2>$null
    Log "Timeout waiting for pod $PodName. Final phase: $phase"
    if ($failedMount) {
        Log "FailedMount events for ${PodName}: $failedMount"
    }

    return $false
}

Log "=== Multi-Contract CSI Test ==="

if ([string]::IsNullOrWhiteSpace($SecondaryContractId)) {
    Log "SKIP: SecondaryContractId was not provided."
    Log "Set -SecondaryContractId to run this test with two different contract IDs."
    return
}

if ($PrimaryContractId -eq $SecondaryContractId) {
    throw "PrimaryContractId and SecondaryContractId must be different."
}

$podStatus = kubectl get pod virtual-cleanroom -o jsonpath='{.status.phase}'
if ($podStatus -ne 'Running') {
    throw "Pre-condition failed: virtual-cleanroom pod is not Running (phase=$podStatus)."
}
Log "virtual-cleanroom pod is Running."

$podJson = kubectl get pod virtual-cleanroom -o json | ConvertFrom-Json
$volumes = $podJson.spec.volumes
$csiVol = $volumes | Where-Object { $_.name -eq 'csi-publisher-input' }
if ($null -eq $csiVol) {
    throw "Could not find csi-publisher-input volume in virtual-cleanroom pod."
}

$attrs = @{}
foreach ($prop in $csiVol.csi.volumeAttributes.PSObject.Properties) {
    $attrs[$prop.Name] = [string]$prop.Value
}

function New-AttrLines {
    param(
        [hashtable]$Attributes,
        [string]$ContractId
    )

    $Attributes['contractId'] = $ContractId

    $attrLines = ""
    foreach ($key in ($Attributes.Keys | Sort-Object)) {
        $attrLines += "          ${key}: `"$($Attributes[$key])`"`n"
    }

    return $attrLines
}

$podA = 'csi-contract-a-test'
$podB = 'csi-contract-b-test'

$manifestA = @"
apiVersion: v1
kind: Pod
metadata:
  name: $podA
spec:
  containers:
    - name: reader
      image: busybox:latest
      command: ["sh", "-c", "sleep 3600"]
      volumeMounts:
        - name: input
          mountPath: /mnt/input
  volumes:
    - name: input
      csi:
        driver: cleanroom.csi.azure.com
        readOnly: true
        volumeAttributes:
$(New-AttrLines -Attributes $attrs -ContractId $PrimaryContractId)  restartPolicy: Never
"@

$manifestB = @"
apiVersion: v1
kind: Pod
metadata:
  name: $podB
spec:
  containers:
    - name: reader
      image: busybox:latest
      command: ["sh", "-c", "sleep 3600"]
      volumeMounts:
        - name: input
          mountPath: /mnt/input
  volumes:
    - name: input
      csi:
        driver: cleanroom.csi.azure.com
        readOnly: true
        volumeAttributes:
$(New-AttrLines -Attributes $attrs -ContractId $SecondaryContractId)  restartPolicy: Never
"@

$tempBasePath = $env:TMPDIR
if ([string]::IsNullOrWhiteSpace($tempBasePath)) {
    $tempBasePath = $env:TEMP
}
if ([string]::IsNullOrWhiteSpace($tempBasePath)) {
    $tempBasePath = $env:TMP
}
if ([string]::IsNullOrWhiteSpace($tempBasePath)) {
    $tempBasePath = [System.IO.Path]::GetTempPath()
}
if ([string]::IsNullOrWhiteSpace($tempBasePath)) {
    $tempBasePath = "/tmp"
}

$tmpA = Join-Path $tempBasePath "csi-contract-a-test.yaml"
$tmpB = Join-Path $tempBasePath "csi-contract-b-test.yaml"

try {
    & {
        $PSNativeCommandUseErrorActionPreference = $false
        kubectl delete pod $podA --force 2>$null
        kubectl delete pod $podB --force 2>$null
    }

    $manifestA | Out-File -FilePath $tmpA -Encoding utf8
    $manifestB | Out-File -FilePath $tmpB -Encoding utf8

    Log "Applying pod manifest A (contractId=$PrimaryContractId)..."
    kubectl apply -f $tmpA | Out-Null

    Log "Applying pod manifest B (contractId=$SecondaryContractId)..."
    kubectl apply -f $tmpB | Out-Null

    Log "Waiting for pod $podA to reach Running..."
    if (-not (Wait-PodRunning -PodName $podA)) {
        throw "Pod $podA did not reach Running within timeout."
    }

    Log "Waiting for pod $podB to reach Running..."
    if (-not (Wait-PodRunning -PodName $podB)) {
        throw "Pod $podB did not reach Running within timeout."
    }

    Log "PASS: both pods reached Running with different contract IDs."
    Log "  podA=$podA contractId=$PrimaryContractId"
    Log "  podB=$podB contractId=$SecondaryContractId"
    Log "=== Multi-Contract CSI Test PASSED ==="
}
finally {
    & {
        $PSNativeCommandUseErrorActionPreference = $false
        kubectl delete pod $podA --force --grace-period=0 2>$null
        kubectl delete pod $podB --force --grace-period=0 2>$null
    }

    Remove-Item $tmpA -ErrorAction SilentlyContinue
    Remove-Item $tmpB -ErrorAction SilentlyContinue
}
