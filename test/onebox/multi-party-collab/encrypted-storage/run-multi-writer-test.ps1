# Test: Concurrent-writer independence for CSI driver.
# Called from run-collab.ps1 after virtual-cleanroom pod is deployed and healthy.
# Deploys a second pod with the same consumer-output volume (readOnly: false).
# Expects BOTH the original and the second writer pod to reach Running state,
# verifying that each gets its own isolated blobfuse2 mount and cache.
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

Log "=== Concurrent-Writer Independence Test ==="

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

# Create second writer pod manifest — same volume, also readOnly: false.
$secondWriterYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: csi-second-writer-test
spec:
  containers:
    - name: writer
      image: busybox:latest
      command: ["sh", "-c", "sleep 3600"]
      volumeMounts:
        - name: output-second
          mountPath: /mnt/output
  volumes:
    - name: output-second
      csi:
        driver: cleanroom.csi.azure.com
        readOnly: false
        volumeAttributes:
$attrLines  restartPolicy: Never
"@

$manifestPath = "$outDir/deployments/csi-second-writer-pod.yaml"
$secondWriterYaml | Out-File $manifestPath
Log "Deploying second writer pod..."

& {
    $PSNativeCommandUseErrorActionPreference = $false
    kubectl delete pod csi-second-writer-test --force 2>$null
}

kubectl apply -f $manifestPath

# Poll for second writer pod to reach Running (not a FailedMount).
Log "Polling for second writer pod Running state (timeout 90s)..."
$timeout = 90
$interval = 5
$elapsed = 0
$secondStatus = ""
while ($elapsed -lt $timeout) {
    $secondStatus = kubectl get pod csi-second-writer-test -o jsonpath='{.status.phase}'
    if ($secondStatus -eq "Running") {
        break
    }
    # Fail fast if the pod enters a terminal failure state.
    if ($secondStatus -eq "Failed") {
        break
    }
    Start-Sleep -Seconds $interval
    $elapsed += $interval
}

# Verify first pod is still healthy.
$firstStatus = kubectl get pod virtual-cleanroom -o jsonpath='{.status.phase}'
Log "First writer (virtual-cleanroom) phase: $firstStatus"
Log "Second writer (csi-second-writer-test) phase: $secondStatus"

# Check CSI driver logs for any single-writer rejection (should NOT appear).
$csiPod = kubectl -n kube-system get pods -l app=cleanroom-csi-driver -o jsonpath='{.items[0].metadata.name}'
$csiLogs = kubectl -n kube-system logs $csiPod -c csi-driver --tail=50

# Cleanup second writer pod — graceful delete so kubelet processes NodeUnstage
# before the pod record is gone. This ensures the CSI driver's staging dir for
# mountID-N is properly cleaned up (refCount→0 → cleanupMountLocked) before the
# recovery test captures the pre-restart mount map.
Log "Cleaning up second writer pod (graceful delete, waiting for full teardown)..."
& {
    $PSNativeCommandUseErrorActionPreference = $false
    kubectl delete pod csi-second-writer-test --grace-period=10 --timeout=60s 2>$null
}
# Wait until the pod is completely gone (kubelet has finished volume teardown).
$deleteTimeout = 60
$deleteElapsed = 0
while ($deleteElapsed -lt $deleteTimeout) {
    & { $PSNativeCommandUseErrorActionPreference = $false; kubectl get pod csi-second-writer-test 2>&1 | Out-Null }
    if ($LASTEXITCODE -ne 0) { break }   # pod record gone
    Start-Sleep -Seconds 3
    $deleteElapsed += 3
}
if ($deleteElapsed -ge $deleteTimeout) {
    Log "WARNING: second writer pod did not fully terminate within ${deleteTimeout}s — forcing cleanup."
    & { $PSNativeCommandUseErrorActionPreference = $false; kubectl delete pod csi-second-writer-test --force 2>$null }
}
Log "Second writer pod fully terminated."

$testPassed = $false
if ($secondStatus -eq "Running" -and $firstStatus -eq "Running") {
    Log "PASS: Both writers reached Running — each has an independent blobfuse2 mount."
    $testPassed = $true
}

if (-not $testPassed) {
    Log "FAIL: Concurrent-writer independence test failed."
    Log "  First writer phase:  $firstStatus"
    Log "  Second writer phase: $secondStatus"
    if ($csiLogs -match "single-writer") {
        Log "  CSI logs contain 'single-writer' rejection — policy was not removed."
    }
    Log "  CSI logs (last 10):"
    $csiLogs -split "`n" | Select-Object -Last 10 | ForEach-Object { Log "    $_" }
    throw "Concurrent-writer independence test FAILED."
}
