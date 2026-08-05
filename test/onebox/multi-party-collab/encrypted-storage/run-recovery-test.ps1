# Tests that the CSI driver correctly recovers its in-memory mount map after
# a pod restart. Strategy:
#   1. Scrape "State dump: mount entry" lines tagged context=post-stage from
#      the running driver's logs  →  this is the ground-truth pre-restart map.
#   2. Kill the CSI driver pod.
#   3. Wait for the replacement pod to become Ready.
#   4. Scrape "State dump: mount entry" lines tagged context=post-recovery from
#      the new pod's logs  →  this is the recovered map.
#   5. Assert every key present pre-restart is present post-recovery with the
#      same stagingPath, refCount, storageKey, readOnly, and pid.
#   6. Assert virtual-cleanroom pod (and all its containers) has zero restarts
#      throughout — confirming the mounts were fully transparent to the app.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Log { param([string]$msg) Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $msg" }

# Parse a logrus text-format field value from a log line.
# e.g.  key="abc" → "abc"    pid=1234 → "1234"
function Get-LogField {
    param([string]$Line, [string]$Field)
    # Use \b (word boundary) before the field name so that e.g. 'stagingPath='
    # does not accidentally match inside 'sseStagingPath=' or 'storageKey='.
    if ($Line -match "\b$Field=`"([^`"]*)`"") { return $Matches[1] }
    if ($Line -match "\b$Field=(\S+)")          { return $Matches[1] }
    return $null
}

# Parse all "State dump: mount entry" lines for the given context tag from a
# block of log text.  Returns a hashtable keyed by mount key.
# $LogText may be a string array (from kubectl 2>&1) or a multi-line string.
function Parse-MountMap {
    param($LogText, [string]$Context)
    $map = @{}
    # Handle both string[] (kubectl output) and a single multi-line string.
    # Using -split '[\r\n]+' on an array element with no embedded newlines is
    # a no-op (returns the element unchanged), so this works for both cases.
    foreach ($line in ($LogText -split '[\r\n]+')) {
        if ($line -notmatch 'State dump: mount entry') { continue }
        if ((Get-LogField $line 'context') -ne $Context)  { continue }
        $key = Get-LogField $line 'mountID'
        if (-not $key) { continue }
        # Skip skeleton entries (staging in-progress): these have staging=true
        # and empty stagingPath. Only keep completed entries (staging=false).
        if ((Get-LogField $line 'staging') -eq 'true') { continue }
        $map[$key] = @{
            stagingPath    = Get-LogField $line 'stagingPath'
            sseStagingPath = Get-LogField $line 'sseStagingPath'
            contractId     = Get-LogField $line 'contractId'
            refCount       = Get-LogField $line 'refCount'
            readOnly       = Get-LogField $line 'readOnly'
            storageKey     = Get-LogField $line 'storageKey'
            pid            = Get-LogField $line 'pid'
        }
    }
    return $map
}

# Parse the current mount state from logs by tracking the last complete
# dumpStateLocked group. Each dumpStateLocked call emits consecutive lines with
# the same context (mutex held); a context transition signals a new group.
# The last group represents the actual mount state at the moment the log ends,
# correctly handling Stage→Unstage→Stage sequences where post-unstage entries
# would otherwise be missed by a context-specific parse.
function Parse-MountMap-Latest {
    param($LogText)
    $currentGroup = @{}
    $lastContext   = $null
    $latestGroup   = @{}

    foreach ($line in ($LogText -split '[\r\n]+')) {
        if ($line -notmatch 'State dump: mount entry') { continue }
        if ((Get-LogField $line 'staging') -eq 'true') { continue }

        $ctx = Get-LogField $line 'context'
        # Context transition — the previous group is complete; promote it.
        if ($ctx -ne $lastContext -and $lastContext -ne $null) {
            $latestGroup  = $currentGroup
            $currentGroup = @{}
        }
        $lastContext = $ctx

        $key = Get-LogField $line 'mountID'
        if (-not $key) { continue }
        $currentGroup[$key] = @{
            stagingPath    = Get-LogField $line 'stagingPath'
            sseStagingPath = Get-LogField $line 'sseStagingPath'
            contractId     = Get-LogField $line 'contractId'
            refCount       = Get-LogField $line 'refCount'
            readOnly       = Get-LogField $line 'readOnly'
            storageKey     = Get-LogField $line 'storageKey'
            pid            = Get-LogField $line 'pid'
        }
    }
    # Promote the final group.
    if ($currentGroup.Count -gt 0) { $latestGroup = $currentGroup }
    return $latestGroup
}

Log "=== CSI Driver Recovery Test ==="

# ── Pre-conditions ────────────────────────────────────────────────────────────
$podStatus = kubectl get pod virtual-cleanroom -o jsonpath='{.status.phase}' 2>&1
if ($podStatus -ne "Running") {
    throw "Pre-condition failed: virtual-cleanroom pod is not Running (phase=$podStatus)."
}
Log "virtual-cleanroom pod is Running."

# Capture container restart counts so we can verify they don't change.
$preRestartCounts = kubectl get pod virtual-cleanroom `
    -o jsonpath='{range .status.containerStatuses[*]}{.name}={.restartCount} {end}'
Log "Container restart counts (pre): $preRestartCounts"

# ── Step 1: capture pre-restart mount map ─────────────────────────────────────
$csiPodPre = kubectl -n kube-system get pods -l app=cleanroom-csi-driver `
    -o jsonpath='{.items[0].metadata.name}'
Log "Current CSI driver pod: $csiPodPre"

$preLogs = kubectl -n kube-system logs $csiPodPre -c csi-driver 2>&1
# Use the last complete dumpStateLocked group as the ground-truth pre-restart map.
# This correctly excludes mounts that were staged and then unstaged before the
# snapshot (e.g. a concurrent-writer test pod that completed before this test ran),
# because Unstage() now emits dumpStateLocked("post-unstage") which starts a new
# group — overriding the earlier post-stage group that included the removed entry.
$preMap = Parse-MountMap-Latest -LogText $preLogs

if ($preMap.Count -eq 0) {
    throw "No 'post-stage' or 'post-recovery' state dump entries found in CSI driver logs. " +
          "Ensure dumpStateLocked is called after Stage()/RecoverMounts() and logging level is Info."
}
Log "Pre-restart mount map ($($preMap.Count) entries):"
foreach ($k in $preMap.Keys) {
    $e = $preMap[$k]
    Log "  mountID=$k  stagingPath=$($e.stagingPath)  contractId=$($e.contractId)  refCount=$($e.refCount)  pid=$($e.pid)  ro=$($e.readOnly)"
}

# ── Step 2: kill CSI driver pod ───────────────────────────────────────────────
Log "Killing CSI driver pod $csiPodPre ..."
kubectl -n kube-system delete pod $csiPodPre --force --grace-period=0 | Out-Null

# Brief pause so the old pod is fully gone before we start waiting for the new one.
Start-Sleep -Seconds 3

# ── Step 3: wait for replacement pod ─────────────────────────────────────────
Log "Waiting for replacement CSI driver pod to be Ready (timeout 120s)..."
kubectl -n kube-system wait `
    --for=condition=Ready pod -l app=cleanroom-csi-driver `
    --timeout=120s

$csiPodPost = kubectl -n kube-system get pods -l app=cleanroom-csi-driver `
    -o jsonpath='{.items[0].metadata.name}'
Log "Replacement pod: $csiPodPost"

# ── Step 4: capture post-restart mount map ────────────────────────────────────
# Give RecoverMounts a moment to finish and flush its logs.
Start-Sleep -Seconds 2
$postLogs = kubectl -n kube-system logs $csiPodPost -c csi-driver 2>&1
$postMap  = Parse-MountMap -LogText $postLogs -Context "post-recovery"

Log "Post-restart mount map ($($postMap.Count) entries):"
foreach ($k in $postMap.Keys) {
    $e = $postMap[$k]
    Log "  mountID=$k  stagingPath=$($e.stagingPath)  contractId=$($e.contractId)  refCount=$($e.refCount)  pid=$($e.pid)  ro=$($e.readOnly)"
}

# ── Step 5: compare maps ──────────────────────────────────────────────────────
$failures = @()

foreach ($key in $preMap.Keys) {
    if (-not $postMap.ContainsKey($key)) {
        $failures += "  MISSING mountID=$key (was in pre-restart map, not in post-recovery map)"
        continue
    }
    $pre  = $preMap[$key]
    $post = $postMap[$key]

    foreach ($field in @('stagingPath', 'contractId', 'refCount', 'storageKey', 'readOnly', 'pid')) {
        if ($pre[$field] -ne $post[$field]) {
            $failures += "  MISMATCH mountID=$key field=$field  pre=[$($pre[$field])]  post=[$($post[$field])]"
        }
    }
}

# Unexpected extra keys in post-recovery (not a failure, but worth logging).
foreach ($key in $postMap.Keys) {
    if (-not $preMap.ContainsKey($key)) {
        Log "  NOTE: mountID=$key appeared in post-recovery but not in pre-restart map (acceptable if it was in-flight)"
    }
}

if ($failures.Count -gt 0) {
    Log "FAIL: mount map mismatch after recovery:"
    $failures | ForEach-Object { Log $_ }
    throw "CSI driver recovery test FAILED."
}
Log "PASS: all $($preMap.Count) mount entries recovered with identical state."

# ── Step 6: app transparency check ────────────────────────────────────────────
$postStatus = kubectl get pod virtual-cleanroom -o jsonpath='{.status.phase}' 2>&1
if ($postStatus -ne "Running") {
    throw "FAIL: virtual-cleanroom pod is not Running after CSI driver restart (phase=$postStatus)."
}

$postRestartCounts = kubectl get pod virtual-cleanroom `
    -o jsonpath='{range .status.containerStatuses[*]}{.name}={.restartCount} {end}'

if ($preRestartCounts -ne $postRestartCounts) {
    throw "FAIL: container restart counts changed during test.`n  pre:  $preRestartCounts`n  post: $postRestartCounts"
}
Log "PASS: virtual-cleanroom pod Running, zero container restarts during test."
Log "  Restart counts: $postRestartCounts"

Log "=== CSI Driver Recovery Test PASSED ==="
