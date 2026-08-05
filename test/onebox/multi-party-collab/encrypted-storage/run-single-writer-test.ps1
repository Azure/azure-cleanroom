# Test: Single-writer enforcement for CSI driver.
# Called from run-collab.ps1 after virtual-cleanroom pod is deployed and healthy.
# Deploys a second pod with the same consumer-output volume (readOnly: false).
# Expects the CSI driver to reject the second mount with a single-writer policy error.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = git rev-parse --show-toplevel
$outDir = "$PSScriptRoot/generated"

function Log {
    param([string]$msg)
    $timestamp = Get-Date -Format "HH:mm:ss"
    Write-Host "[$timestamp] $msg"
}

Log "=== Single-Writer Enforcement Test ==="

# Verify virtual-cleanroom pod is running.
$podStatus = kubectl get pod virtual-cleanroom -o jsonpath='{.status.phase}'
if ($podStatus -ne "Running") {
    throw "virtual-cleanroom pod is not running (status: $podStatus)."
}
Log "virtual-cleanroom pod is Running."

# Extract the consumer-output CSI volume spec from the running pod.
$podJson = kubectl get pod virtual-cleanroom -o json | ConvertFrom-Json
$volumes = $podJson.spec.volumes
$csiVol = $volumes | Where-Object { $_.name -eq "csi-consumer-output" }
if ($null -eq $csiVol) {
    throw "Could not find csi-consumer-output volume in virtual-cleanroom pod."
}

$attrs = $csiVol.csi.volumeAttributes
Log "Found consumer-output volume: account=$($attrs.storageAccount) container=$($attrs.storageContainer)"

# Build volume attributes YAML from the extracted spec (preserves all fields).
$attrLines = ""
foreach ($prop in $attrs.PSObject.Properties) {
    $attrLines += "          $($prop.Name): `"$($prop.Value)`"`n"
}

# Create conflict pod manifest.
$conflictPodYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: csi-writer-conflict-test
spec:
  containers:
    - name: writer
      image: busybox:latest
      command: ["sh", "-c", "sleep 3600"]
      volumeMounts:
        - name: output-conflict
          mountPath: /mnt/output
  volumes:
    - name: output-conflict
      csi:
        driver: cleanroom.csi.azure.com
        readOnly: false
        volumeAttributes:
$attrLines  restartPolicy: Never
"@

$manifestPath = "$outDir/deployments/csi-writer-conflict-pod.yaml"
$conflictPodYaml | Out-File $manifestPath
Log "Deploying conflict writer pod..."

& {
    $PSNativeCommandUseErrorActionPreference = $false
    kubectl delete pod csi-writer-conflict-test --force 2>$null
}

kubectl apply -f $manifestPath

# Poll for FailedMount event instead of sleeping a fixed duration.
Log "Polling for single-writer rejection event (timeout 90s)..."
$timeout = 90
$interval = 5
$elapsed = 0
$events = ""
while ($elapsed -lt $timeout) {
    $events = kubectl get events `
        --field-selector involvedObject.name=csi-writer-conflict-test,reason=FailedMount `
        -o jsonpath='{.items[*].message}'
    if ($events -match "single-writer") {
        break
    }
    Start-Sleep -Seconds $interval
    $elapsed += $interval
}

# Check pod status.
$conflictStatus = kubectl get pod csi-writer-conflict-test -o jsonpath='{.status.phase}'
Log "Conflict pod phase: $conflictStatus"

# Also check CSI driver logs.
$csiPod = kubectl -n kube-system get pods -l app=cleanroom-csi-driver -o jsonpath='{.items[0].metadata.name}'
$csiLogs = kubectl -n kube-system logs $csiPod -c csi-driver --tail=50

$testPassed = $false
if ($events -match "single-writer") {
    Log "PASS: Found single-writer rejection in pod events."
    Log "  Event: $events"
    $testPassed = $true
}
elseif ($csiLogs -match "single-writer policy") {
    Log "PASS: Found single-writer rejection in CSI driver logs."
    $testPassed = $true
}

# Cleanup conflict pod.
Log "Cleaning up conflict test pod..."
& {
    $PSNativeCommandUseErrorActionPreference = $false
    kubectl delete pod csi-writer-conflict-test --force 2>$null
}

if (-not $testPassed) {
    Log "FAIL: Second writer was NOT rejected."
    Log "  Pod phase: $conflictStatus"
    Log "  Events: $events"
    Log "  CSI logs (last 10):"
    $csiLogs -split "`n" | Select-Object -Last 10 | ForEach-Object { Log "    $_" }
    throw "Single-writer enforcement test FAILED."
}

# Verify original pod still healthy.
$originalStatus = kubectl get pod virtual-cleanroom -o jsonpath='{.status.phase}'
if ($originalStatus -ne "Running") {
    throw "Original virtual-cleanroom pod died (status: $originalStatus) during test."
}
Log "Original pod still Running."
Log "=== Single-Writer Enforcement Test PASSED ==="

