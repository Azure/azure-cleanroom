# Test pod policies script for AKS
# This script tests pod scheduling with signed pod policies
# Works with the environment created by deploy-cluster.ps1 with -enableFlexNode

[CmdletBinding()]
param(
    [switch]$Status,
    [switch]$Cleanup,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Script directory
$script:ScriptDir = $PSScriptRoot
$script:SandboxCommon = "$ScriptDir/sandbox_common"
$script:PodPoliciesDir = "$ScriptDir/pod-policies"

# Signing tool and key directory (set up by generate-signing-keys.ps1).
$script:RepoRoot = git rev-parse --show-toplevel
$script:SigningTool = "$RepoRoot/src/k8s-node/api-server-proxy/scripts/policy-signing-tool.sh"
$script:SigningKeyDir = "$SandboxCommon/policy-signing-keys"

# Test result tracking
$script:TestResults = @{}

# Node name (set by Test-ProxyStatus)
$script:VmName = ""

# Kubeconfig from deploy-cluster.ps1
$script:KubeconfigFile = "$SandboxCommon/k8s-credentials.yaml"

function Write-LogInfo {
    param([string]$Message)
    Write-Host "[INFO] $Message" -ForegroundColor Green
}

function Write-LogWarn {
    param([string]$Message)
    Write-Host "[WARN] $Message" -ForegroundColor Yellow
}

function Write-LogError {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

function Write-LogTest {
    param([string]$Message)
    Write-Host "[TEST] $Message" -ForegroundColor Blue
}

function Test-Prerequisites {
    Write-LogInfo "Checking prerequisites..."
    
    # Check required commands
    if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
        Write-LogError "kubectl is required but not installed"
        exit 1
    }
    if (-not (Get-Command curl -ErrorAction SilentlyContinue)) {
        Write-LogError "curl is required but not installed"
        exit 1
    }
    if (-not (Get-Command python3 -ErrorAction SilentlyContinue)) {
        Write-LogError "python3 is required but not installed"
        exit 1
    }
    
    # Use kubeconfig from deploy-cluster.ps1 if it exists
    if (Test-Path $script:KubeconfigFile) {
        Write-LogInfo "Using kubeconfig from: $script:KubeconfigFile"
        $env:KUBECONFIG = $script:KubeconfigFile
    }
    
    # Check if we have a valid kubeconfig
    $null = kubectl cluster-info 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-LogError "Cannot connect to Kubernetes cluster. Make sure kubeconfig is set up correctly."
        exit 1
    }
    
    Write-LogInfo "Prerequisites OK"
}

function Test-SigningKeys {
    Write-LogTest "Checking signing keys..."
    Write-Host ""
    
    # Verify policy-signing-tool.sh exists and keys have been generated.
    if (-not (Test-Path $script:SigningTool)) {
        Write-LogError "policy-signing-tool.sh not found at $script:SigningTool"
        exit 1
    }
    
    $certFile = bash $script:SigningTool --key-dir $script:SigningKeyDir cert 2>&1
    if ($LASTEXITCODE -eq 0 -and (Test-Path $certFile)) {
        Write-LogInfo "Signing keys OK (cert: $certFile)"
    }
    else {
        Write-LogError "Signing keys not found in $script:SigningKeyDir"
        Write-LogError "Make sure deploy-cluster.ps1 was run with -enableFlexNode"
        exit 1
    }
    Write-Host ""
}

function Test-ProxyStatus {
    Write-LogTest "Checking for node with pod-policy label..."
    Write-Host ""
    
    # Get the node with pod-policy label
    $script:VmName = kubectl get nodes -l pod-policy=required -o jsonpath='{.items[0].metadata.name}' 2>&1
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($script:VmName)) {
        Write-LogError "No node found with pod-policy=required label"
        Write-LogError "Make sure deploy-cluster.ps1 was run with -enableKServeInferencing"
        exit 1
    }
    
    Write-LogInfo "Found node with pod-policy label: $script:VmName"
    Write-Host ""
}

function Get-PolicyJson {
    param([string]$PolicyFile)
    
    $pythonScript = @"
import json
import sys

with open('$PolicyFile', 'r') as f:
    policy = json.load(f)

def sort_dict(obj):
    if isinstance(obj, dict):
        return {k: sort_dict(v) for k, v in sorted(obj.items())}
    elif isinstance(obj, list):
        return [sort_dict(item) for item in obj]
    return obj

sorted_policy = sort_dict(policy)
print(json.dumps(sorted_policy, separators=(',', ':')))
"@
    
    $result = python3 -c $pythonScript 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-LogError "Failed to load policy JSON: $result"
        return $null
    }
    return $result
}

function Get-PolicySignature {
    param([string]$PolicyBase64)
    
    try {
        $signature = bash $script:SigningTool --key-dir $script:SigningKeyDir sign $PolicyBase64 2>&1
        
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrEmpty($signature)) {
            return $null
        }
        
        return $signature.Trim()
    }
    catch {
        return $null
    }
}

function Wait-ForPodAdmissionDecision {
    param([string]$PodName)
    
    $maxWaitSeconds = 20
    $pollIntervalSeconds = 5
    $elapsed = 0
    
    Write-LogInfo "Waiting for admission decision (polling every ${pollIntervalSeconds}s, max ${maxWaitSeconds}s)..."
    
    while ($elapsed -lt $maxWaitSeconds) {
        $podStatus = kubectl get pod $PodName -o jsonpath='{.status.phase}' 2>&1
        if ($podStatus -ne "Pending") {
            Write-LogInfo "Pod moved to '$podStatus' state after ${elapsed}s"
            return
        }
        Start-Sleep -Seconds $pollIntervalSeconds
        $elapsed += $pollIntervalSeconds
    }
    
    Write-LogInfo "Max wait time reached (${maxWaitSeconds}s), proceeding with status check..."
}

function Remove-TestResources {
    Write-LogInfo "Cleaning up existing test resources..."
    
    $pods = @(
        "test-signed",
        "test-unsigned",
        "test-bad-sig",
        "test-image-mismatch",
        "test-full-policy",
        "test-command-mismatch",
        "test-env-mismatch",
        "test-volume-mismatch",
        "test-allowall"
    )
    
    foreach ($pod in $pods) {
        kubectl delete pod $pod --ignore-not-found=true --force --grace-period=0 2>&1 | Out-Null
    }
    kubectl delete configmap test-config --ignore-not-found=true 2>&1 | Out-Null
    
    Start-Sleep -Seconds 2
}

function Test-SignedPod {
    Write-LogTest "TEST 1: Creating a SIGNED pod (should be ALLOWED)..."
    Write-Host ""
    
    $policyFile = "$script:PodPoliciesDir/nginx-pod-policy.json"
    if (-not (Test-Path $policyFile)) {
        Write-LogError "Policy file not found: $policyFile"
        $script:TestResults["TEST1"] = "FAILED"
        return
    }
    
    Write-LogInfo "Loading policy from $policyFile"
    $policyJson = Get-PolicyJson -PolicyFile $policyFile
    
    if (-not $policyJson) {
        Write-LogError "Failed to load policy"
        $script:TestResults["TEST1"] = "FAILED"
        return
    }
    
    # Base64 encode the policy
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    Write-LogInfo "Policy: $policyJson"
    
    # Sign the policy
    Write-LogInfo "Signing policy using policy-signing-tool..."
    $signature = Get-PolicySignature -PolicyBase64 $policyBase64
    
    if ([string]::IsNullOrEmpty($signature) -or $signature -eq "null") {
        Write-LogError "Failed to sign policy"
        $script:TestResults["TEST1"] = "FAILED"
        return
    }
    
    # Create the signed pod YAML
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-signed
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "$signature"
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: test
    image: nginx:latest
    ports:
    - containerPort: 80
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Write-LogInfo "Waiting for pod to be scheduled..."
    Start-Sleep -Seconds 10
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-signed -o wide
    Write-Host ""
    
    # Check pod status
    $podStatus = kubectl get pod test-signed -o jsonpath='{.status.phase}' 2>&1
    $podNode = kubectl get pod test-signed -o jsonpath='{.spec.nodeName}' 2>&1
    
    if ($podStatus -in @("Running", "Pending", "ContainerCreating")) {
        if ($podNode -eq $script:VmName) {
            Write-LogInfo "✓ TEST 1 PASSED: Signed pod was allowed on node '$($script:VmName)' (status: $podStatus)"
            $script:TestResults["TEST1"] = "PASSED"
        }
        else {
            Write-LogWarn "✓ TEST 1 PASSED: Signed pod was allowed (status: $podStatus, node: $podNode)"
            $script:TestResults["TEST1"] = "PASSED"
        }
    }
    else {
        Write-LogError "✗ TEST 1 FAILED: Signed pod status is $podStatus"
        kubectl describe pod test-signed
        $script:TestResults["TEST1"] = "FAILED"
    }
    Write-Host ""
}

function Test-UnsignedPod {
    Write-LogTest "TEST 2: Creating an UNSIGNED pod (should be REJECTED)..."
    Write-Host ""
    
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-unsigned
  namespace: default
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: test
    image: nginx:latest
    ports:
    - containerPort: 80
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Wait-ForPodAdmissionDecision -PodName "test-unsigned"
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-unsigned -o wide 2>&1
    Write-Host ""
    
    $podStatus = kubectl get pod test-unsigned -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-unsigned -o jsonpath='{.status.reason}' 2>&1
    $podMessage = kubectl get pod test-unsigned -o jsonpath='{.status.message}' 2>&1
    
    $expectedMsg = "pod policy required but not found"
    
    if ($podStatus -eq "Failed" -and $podReason -eq "NodeAdmissionRejected") {
        if ($podMessage -match $expectedMsg) {
            Write-LogInfo "✓ TEST 2 PASSED: Unsigned pod was REJECTED with expected message"
            Write-Host ""
            kubectl describe pod test-unsigned | Select-String -Pattern "Message:" -Context 0, 3
            $script:TestResults["TEST2"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 2 FAILED: Pod rejected but message mismatch"
            Write-LogError "  Expected: '$expectedMsg'"
            Write-LogError "  Got: '$podMessage'"
            $script:TestResults["TEST2"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed") {
        Write-LogWarn "? TEST 2 PARTIAL: Pod is Failed but reason is '$podReason'"
        kubectl describe pod test-unsigned | Select-String -Pattern "Status:" -Context 0, 5
        $script:TestResults["TEST2"] = "PARTIAL"
    }
    else {
        Write-LogError "✗ TEST 2 FAILED: Unsigned pod was NOT rejected (status: $podStatus)"
        kubectl describe pod test-unsigned
        $script:TestResults["TEST2"] = "FAILED"
    }
    Write-Host ""
}

function Test-BadSignaturePod {
    Write-LogTest "TEST 3: Creating a pod with INVALID signature (should be REJECTED)..."
    Write-Host ""
    
    $policyFile = "$script:PodPoliciesDir/nginx-pod-policy.json"
    $policyJson = Get-PolicyJson -PolicyFile $policyFile
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    # Create pod with valid policy but garbage signature
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-bad-sig
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "aW52YWxpZHNpZ25hdHVyZWRhdGE="
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: test
    image: nginx:latest
    ports:
    - containerPort: 80
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Wait-ForPodAdmissionDecision -PodName "test-bad-sig"
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-bad-sig -o wide 2>&1
    Write-Host ""
    
    $podStatus = kubectl get pod test-bad-sig -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-bad-sig -o jsonpath='{.status.reason}' 2>&1
    $podMessage = kubectl get pod test-bad-sig -o jsonpath='{.status.message}' 2>&1
    
    $expectedMsg = "policy signature verification failed"
    
    if ($podStatus -eq "Failed" -and $podReason -eq "NodeAdmissionRejected") {
        if ($podMessage -match $expectedMsg) {
            Write-LogInfo "✓ TEST 3 PASSED: Pod with bad signature was REJECTED with expected message"
            Write-Host ""
            kubectl describe pod test-bad-sig | Select-String -Pattern "Message:" -Context 0, 3
            $script:TestResults["TEST3"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 3 FAILED: Pod rejected but message mismatch"
            Write-LogError "  Expected: '$expectedMsg'"
            Write-LogError "  Got: '$podMessage'"
            $script:TestResults["TEST3"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed") {
        Write-LogWarn "? TEST 3 PARTIAL: Pod is Failed but reason is '$podReason'"
        $script:TestResults["TEST3"] = "PARTIAL"
    }
    else {
        Write-LogError "✗ TEST 3 FAILED: Pod with bad signature was NOT rejected (status: $podStatus)"
        kubectl describe pod test-bad-sig
        $script:TestResults["TEST3"] = "FAILED"
    }
    Write-Host ""
}

function Test-ImageMismatchPod {
    Write-LogTest "TEST 4: Creating a pod with MISMATCHED IMAGE (policy says nginx, pod uses busybox)..."
    Write-Host ""
    
    $policyFile = "$script:PodPoliciesDir/nginx-pod-policy.json"
    if (-not (Test-Path $policyFile)) {
        Write-LogError "Policy file not found: $policyFile"
        $script:TestResults["TEST4"] = "FAILED"
        return
    }
    
    $policyJson = Get-PolicyJson -PolicyFile $policyFile
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    # Sign the nginx policy
    Write-LogInfo "Signing nginx policy..."
    $signature = Get-PolicySignature -PolicyBase64 $policyBase64
    
    if ([string]::IsNullOrEmpty($signature) -or $signature -eq "null") {
        Write-LogError "Failed to sign policy"
        $script:TestResults["TEST4"] = "FAILED"
        return
    }
    
    Write-LogInfo "Creating pod with busybox:latest but using nginx policy signature..."
    
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-image-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "$signature"
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: test
    image: busybox:latest
    command: ["sleep", "3600"]
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Wait-ForPodAdmissionDecision -PodName "test-image-mismatch"
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-image-mismatch -o wide 2>&1
    Write-Host ""
    
    $podStatus = kubectl get pod test-image-mismatch -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-image-mismatch -o jsonpath='{.status.reason}' 2>&1
    $podMessage = kubectl get pod test-image-mismatch -o jsonpath='{.status.message}' 2>&1
    
    $expectedMsg = "image.*does not match policy"
    
    if ($podStatus -eq "Failed" -and $podReason -eq "NodeAdmissionRejected") {
        if ($podMessage -match $expectedMsg) {
            Write-LogInfo "✓ TEST 4 PASSED: Pod with mismatched image was REJECTED with expected message"
            Write-Host ""
            kubectl describe pod test-image-mismatch | Select-String -Pattern "Message:" -Context 0, 3
            $script:TestResults["TEST4"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 4 FAILED: Pod rejected but message mismatch"
            Write-LogError "  Expected pattern: '$expectedMsg'"
            Write-LogError "  Got: '$podMessage'"
            $script:TestResults["TEST4"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed") {
        Write-LogWarn "? TEST 4 PARTIAL: Pod is Failed but reason is '$podReason'"
        kubectl describe pod test-image-mismatch | Select-String -Pattern "Status:" -Context 0, 5
        $script:TestResults["TEST4"] = "PARTIAL"
    }
    else {
        Write-LogError "✗ TEST 4 FAILED: Pod with mismatched image was NOT rejected (status: $podStatus)"
        kubectl describe pod test-image-mismatch
        $script:TestResults["TEST4"] = "FAILED"
    }
    Write-Host ""
}

function New-TestVolumes {
    Write-LogInfo "Creating test volumes (ConfigMap)..."
    
    $configMapYaml = @"
apiVersion: v1
kind: ConfigMap
metadata:
  name: test-config
  namespace: default
data:
  config.yaml: |
    setting: value
"@
    
    $configMapYaml | kubectl apply -f - 2>&1 | Out-Null
}

function Test-FullPolicyPod {
    Write-LogTest "TEST 5: Creating a pod with FULL POLICY (command, args, env, volumeMounts) - should be ALLOWED..."
    Write-Host ""
    
    # Ensure test volumes exist
    New-TestVolumes
    
    $policyFile = "$script:PodPoliciesDir/full-policy-pod-policy.json"
    if (-not (Test-Path $policyFile)) {
        Write-LogError "Policy file not found: $policyFile"
        $script:TestResults["TEST5"] = "FAILED"
        return
    }
    
    Write-LogInfo "Loading policy from $policyFile"
    $policyJson = Get-PolicyJson -PolicyFile $policyFile
    
    if (-not $policyJson) {
        Write-LogError "Failed to load policy"
        $script:TestResults["TEST5"] = "FAILED"
        return
    }
    
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    Write-LogInfo "Policy: $policyJson"
    
    Write-LogInfo "Signing policy using policy-signing-tool..."
    $signature = Get-PolicySignature -PolicyBase64 $policyBase64
    
    if ([string]::IsNullOrEmpty($signature) -or $signature -eq "null") {
        Write-LogError "Failed to sign policy"
        $script:TestResults["TEST5"] = "FAILED"
        return
    }
    
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-full-policy
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "$signature"
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  volumes:
  - name: config
    configMap:
      name: test-config
  - name: data
    emptyDir: {}
  containers:
  - name: app
    image: busybox:latest
    command: ["/bin/myapp"]
    args: ["--config=/etc/app/config.yaml", "--verbose"]
    env:
    - name: APP_ENV
      value: "production"
    - name: LOG_LEVEL
      value: "debug"
    volumeMounts:
    - name: config
      mountPath: /etc/app
      readOnly: true
    - name: data
      mountPath: /data
      readOnly: false
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Write-LogInfo "Waiting for pod to be scheduled..."
    Start-Sleep -Seconds 10
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-full-policy -o wide
    Write-Host ""
    
    $podStatus = kubectl get pod test-full-policy -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-full-policy -o jsonpath='{.status.reason}' 2>&1
    
    if ($podStatus -in @("Running", "Pending", "ContainerCreating")) {
        if ($podReason -ne "NodeAdmissionRejected") {
            Write-LogInfo "✓ TEST 5 PASSED: Full policy pod was allowed (status: $podStatus)"
            $script:TestResults["TEST5"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 5 FAILED: Full policy pod was rejected"
            kubectl describe pod test-full-policy
            $script:TestResults["TEST5"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed" -and $podReason -ne "NodeAdmissionRejected") {
        Write-LogInfo "✓ TEST 5 PASSED: Full policy pod was admitted (failed later due to: $podReason)"
        $script:TestResults["TEST5"] = "PASSED"
    }
    else {
        Write-LogError "✗ TEST 5 FAILED: Full policy pod status is $podStatus (reason: $podReason)"
        kubectl describe pod test-full-policy
        $script:TestResults["TEST5"] = "FAILED"
    }
    Write-Host ""
}

function Test-CommandMismatchPod {
    Write-LogTest "TEST 6: Creating a pod with MISMATCHED COMMAND (should be REJECTED)..."
    Write-Host ""
    
    $policyFile = "$script:PodPoliciesDir/full-policy-pod-policy.json"
    $policyJson = Get-PolicyJson -PolicyFile $policyFile
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    $signature = Get-PolicySignature -PolicyBase64 $policyBase64
    
    if ([string]::IsNullOrEmpty($signature) -or $signature -eq "null") {
        Write-LogError "Failed to sign policy"
        $script:TestResults["TEST6"] = "FAILED"
        return
    }
    
    Write-LogInfo "Creating pod with different command (policy expects /bin/myapp, using /bin/sh)..."
    
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-command-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "$signature"
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  volumes:
  - name: config
    configMap:
      name: test-config
  - name: data
    emptyDir: {}
  containers:
  - name: app
    image: busybox:latest
    command: ["/bin/sh"]
    args: ["--config=/etc/app/config.yaml", "--verbose"]
    env:
    - name: APP_ENV
      value: "production"
    - name: LOG_LEVEL
      value: "debug"
    volumeMounts:
    - name: config
      mountPath: /etc/app
      readOnly: true
    - name: data
      mountPath: /data
      readOnly: false
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Wait-ForPodAdmissionDecision -PodName "test-command-mismatch"
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-command-mismatch -o wide 2>&1
    Write-Host ""
    
    $podStatus = kubectl get pod test-command-mismatch -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-command-mismatch -o jsonpath='{.status.reason}' 2>&1
    $podMessage = kubectl get pod test-command-mismatch -o jsonpath='{.status.message}' 2>&1
    
    $expectedMsg = "command.*does not match policy"
    
    if ($podStatus -eq "Failed" -and $podReason -eq "NodeAdmissionRejected") {
        if ($podMessage -match $expectedMsg) {
            Write-LogInfo "✓ TEST 6 PASSED: Pod with command mismatch was REJECTED with expected message"
            Write-Host ""
            kubectl describe pod test-command-mismatch | Select-String -Pattern "Message:" -Context 0, 3
            $script:TestResults["TEST6"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 6 FAILED: Pod rejected but message mismatch"
            Write-LogError "  Expected pattern: '$expectedMsg'"
            Write-LogError "  Got: '$podMessage'"
            $script:TestResults["TEST6"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed") {
        Write-LogWarn "? TEST 6 PARTIAL: Pod is Failed but reason is '$podReason'"
        $script:TestResults["TEST6"] = "PARTIAL"
    }
    else {
        Write-LogError "✗ TEST 6 FAILED: Pod with command mismatch was NOT rejected (status: $podStatus)"
        kubectl describe pod test-command-mismatch
        $script:TestResults["TEST6"] = "FAILED"
    }
    Write-Host ""
}

function Test-EnvMismatchPod {
    Write-LogTest "TEST 7: Creating a pod with MISMATCHED ENV (should be REJECTED)..."
    Write-Host ""
    
    $policyFile = "$script:PodPoliciesDir/full-policy-pod-policy.json"
    $policyJson = Get-PolicyJson -PolicyFile $policyFile
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    $signature = Get-PolicySignature -PolicyBase64 $policyBase64
    
    if ([string]::IsNullOrEmpty($signature) -or $signature -eq "null") {
        Write-LogError "Failed to sign policy"
        $script:TestResults["TEST7"] = "FAILED"
        return
    }
    
    Write-LogInfo "Creating pod with different env (policy expects APP_ENV=production, using APP_ENV=development)..."
    
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-env-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "$signature"
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  volumes:
  - name: config
    configMap:
      name: test-config
  - name: data
    emptyDir: {}
  containers:
  - name: app
    image: busybox:latest
    command: ["/bin/myapp"]
    args: ["--config=/etc/app/config.yaml", "--verbose"]
    env:
    - name: APP_ENV
      value: "development"
    - name: LOG_LEVEL
      value: "debug"
    volumeMounts:
    - name: config
      mountPath: /etc/app
      readOnly: true
    - name: data
      mountPath: /data
      readOnly: false
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Wait-ForPodAdmissionDecision -PodName "test-env-mismatch"
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-env-mismatch -o wide 2>&1
    Write-Host ""
    
    $podStatus = kubectl get pod test-env-mismatch -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-env-mismatch -o jsonpath='{.status.reason}' 2>&1
    $podMessage = kubectl get pod test-env-mismatch -o jsonpath='{.status.message}' 2>&1
    
    $expectedMsg = "env var.*does not match policy"
    
    if ($podStatus -eq "Failed" -and $podReason -eq "NodeAdmissionRejected") {
        if ($podMessage -match $expectedMsg) {
            Write-LogInfo "✓ TEST 7 PASSED: Pod with env mismatch was REJECTED with expected message"
            Write-Host ""
            kubectl describe pod test-env-mismatch | Select-String -Pattern "Message:" -Context 0, 3
            $script:TestResults["TEST7"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 7 FAILED: Pod rejected but message mismatch"
            Write-LogError "  Expected pattern: '$expectedMsg'"
            Write-LogError "  Got: '$podMessage'"
            $script:TestResults["TEST7"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed") {
        Write-LogWarn "? TEST 7 PARTIAL: Pod is Failed but reason is '$podReason'"
        $script:TestResults["TEST7"] = "PARTIAL"
    }
    else {
        Write-LogError "✗ TEST 7 FAILED: Pod with env mismatch was NOT rejected (status: $podStatus)"
        kubectl describe pod test-env-mismatch
        $script:TestResults["TEST7"] = "FAILED"
    }
    Write-Host ""
}

function Test-VolumeMismatchPod {
    Write-LogTest "TEST 8: Creating a pod with MISMATCHED VOLUME MOUNT (should be REJECTED)..."
    Write-Host ""
    
    $policyFile = "$script:PodPoliciesDir/full-policy-pod-policy.json"
    $policyJson = Get-PolicyJson -PolicyFile $policyFile
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    $signature = Get-PolicySignature -PolicyBase64 $policyBase64
    
    if ([string]::IsNullOrEmpty($signature) -or $signature -eq "null") {
        Write-LogError "Failed to sign policy"
        $script:TestResults["TEST8"] = "FAILED"
        return
    }
    
    Write-LogInfo "Creating pod with different volume mount path (policy expects /etc/app, using /etc/config)..."
    
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-volume-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "$signature"
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  volumes:
  - name: config
    configMap:
      name: test-config
  - name: data
    emptyDir: {}
  containers:
  - name: app
    image: busybox:latest
    command: ["/bin/myapp"]
    args: ["--config=/etc/app/config.yaml", "--verbose"]
    env:
    - name: APP_ENV
      value: "production"
    - name: LOG_LEVEL
      value: "debug"
    volumeMounts:
    - name: config
      mountPath: /etc/config
      readOnly: true
    - name: data
      mountPath: /data
      readOnly: false
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Wait-ForPodAdmissionDecision -PodName "test-volume-mismatch"
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-volume-mismatch -o wide 2>&1
    Write-Host ""
    
    $podStatus = kubectl get pod test-volume-mismatch -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-volume-mismatch -o jsonpath='{.status.reason}' 2>&1
    $podMessage = kubectl get pod test-volume-mismatch -o jsonpath='{.status.message}' 2>&1
    
    $expectedMsg = "volume mount.*does not match policy"
    
    if ($podStatus -eq "Failed" -and $podReason -eq "NodeAdmissionRejected") {
        if ($podMessage -match $expectedMsg) {
            Write-LogInfo "✓ TEST 8 PASSED: Pod with volume mount mismatch was REJECTED with expected message"
            Write-Host ""
            kubectl describe pod test-volume-mismatch | Select-String -Pattern "Message:" -Context 0, 3
            $script:TestResults["TEST8"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 8 FAILED: Pod rejected but message mismatch"
            Write-LogError "  Expected pattern: '$expectedMsg'"
            Write-LogError "  Got: '$podMessage'"
            $script:TestResults["TEST8"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed") {
        Write-LogWarn "? TEST 8 PARTIAL: Pod is Failed but reason is '$podReason'"
        $script:TestResults["TEST8"] = "PARTIAL"
    }
    else {
        Write-LogError "✗ TEST 8 FAILED: Pod with volume mount mismatch was NOT rejected (status: $podStatus)"
        kubectl describe pod test-volume-mismatch
        $script:TestResults["TEST8"] = "FAILED"
    }
    Write-Host ""
}

function Test-AllowAllPod {
    Write-LogTest "TEST 9: Creating a pod with ALLOWALL policy (should be ALLOWED without validation)..."
    Write-Host ""
    
    # Create the allowall policy: ["allowall"]
    $policyJson = '["allowall"]'
    
    # Base64 encode the policy
    $policyBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($policyJson))
    
    Write-LogInfo "Policy: $policyJson"
    Write-LogInfo "Policy base64: $policyBase64"
    
    # Sign the policy
    Write-LogInfo "Signing allowall policy using policy-signing-tool..."
    $signature = Get-PolicySignature -PolicyBase64 $policyBase64
    
    if ([string]::IsNullOrEmpty($signature) -or $signature -eq "null") {
        Write-LogError "Failed to sign policy"
        $script:TestResults["TEST9"] = "FAILED"
        return
    }
    
    # Create a pod with any spec - it should be allowed because of allowall policy
    # Using a completely different image/command that wouldn't match any normal policy
    $podYaml = @"
apiVersion: v1
kind: Pod
metadata:
  name: test-allowall
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policyBase64"
    api-server-proxy.io/signature: "$signature"
spec:
  nodeSelector:
    pod-policy: "required"
  tolerations:
  - key: "pod-policy"
    operator: "Equal"
    value: "required"
    effect: "NoSchedule"
  containers:
  - name: any-container
    image: alpine:latest
    command: ["sleep", "3600"]
    env:
    - name: ANY_VAR
      value: "any_value"
"@
    
    $podYaml | kubectl apply -f - 2>&1 | Out-Null
    
    Write-LogInfo "Waiting for pod to be scheduled..."
    Start-Sleep -Seconds 10
    
    Write-Host ""
    Write-Host "Pod status:"
    kubectl get pod test-allowall -o wide
    Write-Host ""
    
    # Check pod status
    $podStatus = kubectl get pod test-allowall -o jsonpath='{.status.phase}' 2>&1
    $podReason = kubectl get pod test-allowall -o jsonpath='{.status.reason}' 2>&1
    $podNode = kubectl get pod test-allowall -o jsonpath='{.spec.nodeName}' 2>&1
    
    if ($podStatus -in @("Running", "Pending", "ContainerCreating")) {
        if ($podReason -ne "NodeAdmissionRejected") {
            Write-LogInfo "✓ TEST 9 PASSED: Allowall policy pod was allowed without validation (status: $podStatus, node: $podNode)"
            $script:TestResults["TEST9"] = "PASSED"
        }
        else {
            Write-LogError "✗ TEST 9 FAILED: Allowall policy pod was rejected"
            kubectl describe pod test-allowall
            $script:TestResults["TEST9"] = "FAILED"
        }
    }
    elseif ($podStatus -eq "Failed" -and $podReason -ne "NodeAdmissionRejected") {
        # Pod failed for other reasons - that's OK, it was admitted
        Write-LogInfo "✓ TEST 9 PASSED: Allowall policy pod was admitted (failed later due to: $podReason)"
        $script:TestResults["TEST9"] = "PASSED"
    }
    else {
        Write-LogError "✗ TEST 9 FAILED: Allowall policy pod status is $podStatus (reason: $podReason)"
        kubectl describe pod test-allowall
        $script:TestResults["TEST9"] = "FAILED"
    }
    Write-Host ""
}

function Invoke-Tests {
    Write-Host ""
    Write-Host "========================================"
    Write-Host "  Pod Policy Verification Tests (AKS)"
    Write-Host "========================================"
    Write-Host ""
    
    Test-Prerequisites
    Test-SigningKeys
    Test-ProxyStatus
    Remove-TestResources
    Test-SignedPod
    Test-UnsignedPod
    Test-BadSignaturePod
    Test-ImageMismatchPod
    Test-FullPolicyPod
    Test-CommandMismatchPod
    Test-EnvMismatchPod
    Test-VolumeMismatchPod
    Test-AllowAllPod
    
    Write-Host ""
    Write-Host "========================================"
    Write-Host "  Test Results Summary"
    Write-Host "========================================"
    Write-Host ""
    
    $passed = 0
    $failed = 0
    $partial = 0
    
    $testNames = @{
        "TEST1" = "Signed pod allowed"
        "TEST2" = "Unsigned pod rejected"
        "TEST3" = "Bad signature rejected"
        "TEST4" = "Image mismatch rejected"
        "TEST5" = "Full policy pod allowed"
        "TEST6" = "Command mismatch rejected"
        "TEST7" = "Env mismatch rejected"
        "TEST8" = "Volume mismatch rejected"
        "TEST9" = "Allowall policy allowed"
    }
    
    foreach ($test in @("TEST1", "TEST2", "TEST3", "TEST4", "TEST5", "TEST6", "TEST7", "TEST8", "TEST9")) {
        $result = $script:TestResults[$test]
        $name = $testNames[$test]
        $paddedName = $name.PadRight(28)
        
        switch ($result) {
            "PASSED" {
                Write-Host "  " -NoNewline
                Write-Host "✓" -ForegroundColor Green -NoNewline
                Write-Host " ${test}: $paddedName - " -NoNewline
                Write-Host "PASSED" -ForegroundColor Green
                $passed++
            }
            "PARTIAL" {
                Write-Host "  " -NoNewline
                Write-Host "?" -ForegroundColor Yellow -NoNewline
                Write-Host " ${test}: $paddedName - " -NoNewline
                Write-Host "PARTIAL" -ForegroundColor Yellow
                $partial++
            }
            default {
                Write-Host "  " -NoNewline
                Write-Host "✗" -ForegroundColor Red -NoNewline
                Write-Host " ${test}: $paddedName - " -NoNewline
                Write-Host "FAILED" -ForegroundColor Red
                $failed++
            }
        }
    }
    
    Write-Host ""
    Write-Host "----------------------------------------"
    if ($failed -eq 0 -and $partial -eq 0) {
        Write-Host "  All $passed tests PASSED!" -ForegroundColor Green
    }
    elseif ($failed -eq 0) {
        Write-Host "  $passed passed, $partial partial" -ForegroundColor Yellow
    }
    else {
        Write-Host "  $passed passed, $failed failed, $partial partial" -ForegroundColor Red
    }
    Write-Host "----------------------------------------"
    Write-Host ""
    Write-Host "To clean up test resources:"
    Write-Host "  kubectl delete pod test-signed test-unsigned test-bad-sig test-image-mismatch test-full-policy test-command-mismatch test-env-mismatch test-volume-mismatch test-allowall"
    Write-Host ""
    
    if ($failed -gt 0) {
        exit 1
    }
}

function Show-Help {
    Write-Host "Usage: test-pod-policies.ps1 [options]"
    Write-Host ""
    Write-Host "Tests pod scheduling with signed pod policies on AKS."
    Write-Host "Works with the environment created by deploy-cluster.ps1 with -enableKServeInferencing."
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -Status      Check signing keys and proxy status only"
    Write-Host "  -Cleanup     Clean up test resources only"
    Write-Host "  -Help        Show this help message"
    Write-Host ""
}

# Main
if ($Help) {
    Show-Help
    exit 0
}

if ($Status) {
    Test-Prerequisites
    Test-SigningKeys
    Test-ProxyStatus
    exit 0
}

if ($Cleanup) {
    Remove-TestResources
    Write-LogInfo "Test resources cleaned up"
    exit 0
}

Invoke-Tests
