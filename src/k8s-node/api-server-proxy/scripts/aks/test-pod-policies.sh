#!/bin/bash
# Test pod policies script for AKS
# This script tests pod scheduling with signed pod policies
# Assumes deploy-cluster.sh, deploy-flex-node-vm.sh, and deploy-api-server-proxy.sh were run previously

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Logging functions
log_info() { echo -e "${GREEN}[INFO]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }
log_test() { echo -e "${BLUE}[TEST]${NC} $1"; }

# Script directory
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GENERATED_DIR="$SCRIPT_DIR/generated"
PROJECT_ROOT="$(dirname "$(dirname "$SCRIPT_DIR")")"
POD_POLICIES_DIR="$SCRIPT_DIR/../pod-policies"
SIGNING_TOOL="${SCRIPT_DIR}/../policy-signing-tool.sh"
SIGNING_KEY_DIR="${GENERATED_DIR}/policy-signing-keys"

# Use kubeconfig from generated folder.
export KUBECONFIG="$GENERATED_DIR/kubeconfig"

# Test result tracking
TEST1_RESULT=""
TEST2_RESULT=""
TEST3_RESULT=""
TEST4_RESULT=""
TEST5_RESULT=""
TEST6_RESULT=""
TEST7_RESULT=""
TEST8_RESULT=""
TEST9_RESULT=""

# Node name (set by check_proxy_status)
VM_NAME=""

# Load default values from cluster config if it exists
if [[ -f "$GENERATED_DIR/cluster-config.json" ]]; then
    CONFIG_FILE="$GENERATED_DIR/cluster-config.json"
    RESOURCE_GROUP=$(jq -r '.resourceGroup // empty' "$CONFIG_FILE" 2>/dev/null || echo "")
    CLUSTER_NAME=$(jq -r '.clusterName // empty' "$CONFIG_FILE" 2>/dev/null || echo "")
fi

check_prerequisites() {
    log_info "Checking prerequisites..."
    
    # Check required commands
    command -v kubectl >/dev/null 2>&1 || { log_error "kubectl is required but not installed"; exit 1; }
    command -v curl >/dev/null 2>&1 || { log_error "curl is required but not installed"; exit 1; }
    command -v jq >/dev/null 2>&1 || { log_error "jq is required but not installed"; exit 1; }
    command -v az >/dev/null 2>&1 || { log_error "az CLI is required but not installed"; exit 1; }
    command -v python3 >/dev/null 2>&1 || { log_error "python3 is required but not installed"; exit 1; }
    
    # Check if we have a valid kubeconfig
    if [[ ! -f "$KUBECONFIG" ]]; then
        log_error "Kubeconfig not found at: $KUBECONFIG"
        log_error "Make sure deploy-cluster.sh was run successfully"
        exit 1
    fi
    kubectl cluster-info &>/dev/null || { log_error "Cannot connect to Kubernetes cluster. Make sure kubeconfig is set up correctly."; exit 1; }
    
    log_info "Prerequisites OK"
}

check_signing_keys() {
    log_test "Checking signing keys..."
    echo ""
    
    if [[ -f "$SIGNING_KEY_DIR/signing-key.pem" && -f "$SIGNING_KEY_DIR/signing-cert.pem" ]]; then
        log_info "Signing keys found in $SIGNING_KEY_DIR"
    else
        log_error "Signing keys not found in $SIGNING_KEY_DIR"
        log_error "Make sure deploy-api-server-proxy.sh was run successfully"
        exit 1
    fi
    echo ""
}

check_proxy_status() {
    log_test "Checking for node with pod-policy label..."
    echo ""
    
    # Get the node with pod-policy label
    VM_NAME=$(kubectl get nodes -l pod-policy=required -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || echo "")
    
    if [[ -z "$VM_NAME" ]]; then
        log_error "No node found with pod-policy=required label"
        log_error "Make sure deploy-cluster.sh and deploy-flex-node-vm.sh were run successfully"
        exit 1
    fi
    
    log_info "Found node with pod-policy label: $VM_NAME"
    echo ""
}

# Load and compact a policy JSON file (sorted keys, no whitespace)
load_policy_json() {
    local policy_file="$1"
    python3 -c "
import json
import sys

with open('$policy_file', 'r') as f:
    policy = json.load(f)

def sort_dict(obj):
    if isinstance(obj, dict):
        return {k: sort_dict(v) for k, v in sorted(obj.items())}
    elif isinstance(obj, list):
        return [sort_dict(item) for item in obj]
    return obj

sorted_policy = sort_dict(policy)
print(json.dumps(sorted_policy, separators=(',', ':')))
"
}

# Sign a policy JSON and return the signature
sign_policy() {
    local policy_base64="$1"
    
    local signature
    signature=$("$SIGNING_TOOL" --key-dir "$SIGNING_KEY_DIR" sign "$policy_base64")
    
    if [[ $? -ne 0 || -z "$signature" ]]; then
        echo ""
        return 1
    fi
    
    echo "$signature"
}

cleanup_test_resources() {
    log_info "Cleaning up existing test resources..."
    kubectl delete pod test-signed --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-unsigned --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-bad-sig --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-image-mismatch --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-full-policy --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-command-mismatch --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-env-mismatch --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-volume-mismatch --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete pod test-allowall --ignore-not-found=true --force --grace-period=0 2>/dev/null || true
    kubectl delete configmap test-config --ignore-not-found=true 2>/dev/null || true
    sleep 2
}

test_signed_pod() {
    log_test "TEST 1: Creating a SIGNED pod (should be ALLOWED)..."
    echo ""
    
    # Load the nginx policy from the checked-in file
    local policy_file="$POD_POLICIES_DIR/nginx-pod-policy.json"
    if [[ ! -f "$policy_file" ]]; then
        log_error "Policy file not found: $policy_file"
        TEST1_RESULT="FAILED"
        return
    fi
    
    log_info "Loading policy from $policy_file"
    local policy_json
    policy_json=$(load_policy_json "$policy_file")
    
    # Base64 encode the policy
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    log_info "Policy: $policy_json"
    
    # Sign the policy
    log_info "Signing policy using policy-signing-tool..."
    local signature
    signature=$(sign_policy "$policy_base64")
    
    if [[ -z "$signature" || "$signature" == "null" ]]; then
        log_error "Failed to sign policy"
        TEST1_RESULT="FAILED"
        return
    fi
    
    # Create the signed pod YAML
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-signed
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for pod to be scheduled..."
    sleep 10
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-signed -o wide
    echo ""
    
    # Check pod status
    POD_STATUS=$(kubectl get pod test-signed -o jsonpath='{.status.phase}')
    POD_NODE=$(kubectl get pod test-signed -o jsonpath='{.spec.nodeName}' 2>/dev/null || echo "")
    if [[ "$POD_STATUS" == "Running" || "$POD_STATUS" == "Pending" || "$POD_STATUS" == "ContainerCreating" ]]; then
        if [[ "$POD_NODE" == "$VM_NAME" ]]; then
            log_info "✓ TEST 1 PASSED: Signed pod was allowed on node '$VM_NAME' (status: $POD_STATUS)"
            TEST1_RESULT="PASSED"
        else
            log_warn "✓ TEST 1 PASSED: Signed pod was allowed (status: $POD_STATUS, node: $POD_NODE)"
            TEST1_RESULT="PASSED"
        fi
    else
        log_error "✗ TEST 1 FAILED: Signed pod status is $POD_STATUS"
        kubectl describe pod test-signed
        TEST1_RESULT="FAILED"
    fi
    echo ""
}

test_unsigned_pod() {
    log_test "TEST 2: Creating an UNSIGNED pod (should be REJECTED)..."
    echo ""
    
    # Create unsigned pod yaml directly
    cat <<EOF | kubectl apply -f -
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
EOF
    
    log_info "Waiting for admission decision..."
    sleep 5
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-unsigned -o wide 2>/dev/null || echo "Pod not found"
    echo ""
    
    # Check if pod is in Failed state
    POD_STATUS=$(kubectl get pod test-unsigned -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
    POD_REASON=$(kubectl get pod test-unsigned -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    POD_MESSAGE=$(kubectl get pod test-unsigned -o jsonpath='{.status.message}' 2>/dev/null || echo "")
    
    EXPECTED_MSG="pod policy required but not found"
    if [[ "$POD_STATUS" == "Failed" && "$POD_REASON" == "NodeAdmissionRejected" ]]; then
        if echo "$POD_MESSAGE" | grep -q "$EXPECTED_MSG"; then
            log_info "✓ TEST 2 PASSED: Unsigned pod was REJECTED with expected message"
            echo ""
            kubectl describe pod test-unsigned | grep -A3 "Message:"
            TEST2_RESULT="PASSED"
        else
            log_error "✗ TEST 2 FAILED: Pod rejected but message mismatch"
            log_error "  Expected: '$EXPECTED_MSG'"
            log_error "  Got: '$POD_MESSAGE'"
            TEST2_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" ]]; then
        log_warn "? TEST 2 PARTIAL: Pod is Failed but reason is '$POD_REASON'"
        kubectl describe pod test-unsigned | grep -A5 "Status:"
        TEST2_RESULT="PARTIAL"
    else
        log_error "✗ TEST 2 FAILED: Unsigned pod was NOT rejected (status: $POD_STATUS)"
        kubectl describe pod test-unsigned
        TEST2_RESULT="FAILED"
    fi
    echo ""
}

test_bad_signature_pod() {
    log_test "TEST 3: Creating a pod with INVALID signature (should be REJECTED)..."
    echo ""
    
    # Load the nginx policy from the checked-in file
    local policy_file="$POD_POLICIES_DIR/nginx-pod-policy.json"
    local policy_json
    policy_json=$(load_policy_json "$policy_file")
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    # Create pod with valid policy but garbage signature
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-bad-sig
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for admission decision..."
    sleep 5
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-bad-sig -o wide 2>/dev/null || echo "Pod not found"
    echo ""
    
    # Check if pod is in Failed state
    POD_STATUS=$(kubectl get pod test-bad-sig -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
    POD_REASON=$(kubectl get pod test-bad-sig -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    POD_MESSAGE=$(kubectl get pod test-bad-sig -o jsonpath='{.status.message}' 2>/dev/null || echo "")
    
    EXPECTED_MSG="policy signature verification failed"
    if [[ "$POD_STATUS" == "Failed" && "$POD_REASON" == "NodeAdmissionRejected" ]]; then
        if echo "$POD_MESSAGE" | grep -q "$EXPECTED_MSG"; then
            log_info "✓ TEST 3 PASSED: Pod with bad signature was REJECTED with expected message"
            echo ""
            kubectl describe pod test-bad-sig | grep -A3 "Message:"
            TEST3_RESULT="PASSED"
        else
            log_error "✗ TEST 3 FAILED: Pod rejected but message mismatch"
            log_error "  Expected: '$EXPECTED_MSG'"
            log_error "  Got: '$POD_MESSAGE'"
            TEST3_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" ]]; then
        log_warn "? TEST 3 PARTIAL: Pod is Failed but reason is '$POD_REASON'"
        TEST3_RESULT="PARTIAL"
    else
        log_error "✗ TEST 3 FAILED: Pod with bad signature was NOT rejected (status: $POD_STATUS)"
        kubectl describe pod test-bad-sig
        TEST3_RESULT="FAILED"
    fi
    echo ""
}

test_image_mismatch_pod() {
    log_test "TEST 4: Creating a pod with MISMATCHED IMAGE (policy says nginx, pod uses busybox)..."
    echo ""
    
    # Load the nginx policy from the checked-in file (this policy allows nginx:latest)
    local policy_file="$POD_POLICIES_DIR/nginx-pod-policy.json"
    if [[ ! -f "$policy_file" ]]; then
        log_error "Policy file not found: $policy_file"
        TEST4_RESULT="FAILED"
        return
    fi
    
    local policy_json
    policy_json=$(load_policy_json "$policy_file")
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    # Sign the nginx policy
    log_info "Signing nginx policy..."
    local signature
    signature=$(sign_policy "$policy_base64")
    
    if [[ -z "$signature" || "$signature" == "null" ]]; then
        log_error "Failed to sign policy"
        TEST4_RESULT="FAILED"
        return
    fi
    
    log_info "Creating pod with busybox:latest but using nginx policy signature..."
    
    # Create a pod with busybox image but using the nginx policy signature
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-image-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for admission decision..."
    sleep 5
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-image-mismatch -o wide 2>/dev/null || echo "Pod not found"
    echo ""
    
    # Check if pod is in Failed state
    POD_STATUS=$(kubectl get pod test-image-mismatch -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
    POD_REASON=$(kubectl get pod test-image-mismatch -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    POD_MESSAGE=$(kubectl get pod test-image-mismatch -o jsonpath='{.status.message}' 2>/dev/null || echo "")
    
    EXPECTED_MSG="image.*does not match policy"
    if [[ "$POD_STATUS" == "Failed" && "$POD_REASON" == "NodeAdmissionRejected" ]]; then
        if echo "$POD_MESSAGE" | grep -qE "$EXPECTED_MSG"; then
            log_info "✓ TEST 4 PASSED: Pod with mismatched image was REJECTED with expected message"
            echo ""
            kubectl describe pod test-image-mismatch | grep -A3 "Message:"
            TEST4_RESULT="PASSED"
        else
            log_error "✗ TEST 4 FAILED: Pod rejected but message mismatch"
            log_error "  Expected pattern: '$EXPECTED_MSG'"
            log_error "  Got: '$POD_MESSAGE'"
            TEST4_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" ]]; then
        log_warn "? TEST 4 PARTIAL: Pod is Failed but reason is '$POD_REASON'"
        kubectl describe pod test-image-mismatch | grep -A5 "Status:"
        TEST4_RESULT="PARTIAL"
    else
        log_error "✗ TEST 4 FAILED: Pod with mismatched image was NOT rejected (status: $POD_STATUS)"
        kubectl describe pod test-image-mismatch
        TEST4_RESULT="FAILED"
    fi
    echo ""
}

# Helper to create test volumes (ConfigMap)
create_test_volumes() {
    log_info "Creating test volumes (ConfigMap)..."
    
    # Create a ConfigMap for the config volume
    kubectl apply -f - <<EOF
apiVersion: v1
kind: ConfigMap
metadata:
  name: test-config
  namespace: default
data:
  config.yaml: |
    setting: value
EOF
}

test_full_policy_pod() {
    log_test "TEST 5: Creating a pod with FULL POLICY (command, args, env, volumeMounts) - should be ALLOWED..."
    echo ""
    
    # Ensure test volumes exist
    create_test_volumes
    
    # Load the full policy from the checked-in file
    local policy_file="$POD_POLICIES_DIR/full-policy-pod-policy.json"
    if [[ ! -f "$policy_file" ]]; then
        log_error "Policy file not found: $policy_file"
        TEST5_RESULT="FAILED"
        return
    fi
    
    log_info "Loading policy from $policy_file"
    local policy_json
    policy_json=$(load_policy_json "$policy_file")
    
    # Base64 encode the policy
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    log_info "Policy: $policy_json"
    
    # Sign the policy
    log_info "Signing policy using policy-signing-tool..."
    local signature
    signature=$(sign_policy "$policy_base64")
    
    if [[ -z "$signature" || "$signature" == "null" ]]; then
        log_error "Failed to sign policy"
        TEST5_RESULT="FAILED"
        return
    fi
    
    # Create the signed pod YAML with all fields matching policy
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-full-policy
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for pod to be scheduled..."
    sleep 10
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-full-policy -o wide
    echo ""
    
    # Check pod status
    POD_STATUS=$(kubectl get pod test-full-policy -o jsonpath='{.status.phase}')
    POD_REASON=$(kubectl get pod test-full-policy -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    if [[ "$POD_STATUS" == "Running" || "$POD_STATUS" == "Pending" || "$POD_STATUS" == "ContainerCreating" ]]; then
        # For busybox with fake command, it will fail but should be admitted first
        if [[ "$POD_REASON" != "NodeAdmissionRejected" ]]; then
            log_info "✓ TEST 5 PASSED: Full policy pod was allowed (status: $POD_STATUS)"
            TEST5_RESULT="PASSED"
        else
            log_error "✗ TEST 5 FAILED: Full policy pod was rejected"
            kubectl describe pod test-full-policy
            TEST5_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" && "$POD_REASON" != "NodeAdmissionRejected" ]]; then
        # Pod failed for other reasons (e.g., command not found) - that's OK, it was admitted
        log_info "✓ TEST 5 PASSED: Full policy pod was admitted (failed later due to: $POD_REASON)"
        TEST5_RESULT="PASSED"
    else
        log_error "✗ TEST 5 FAILED: Full policy pod status is $POD_STATUS (reason: $POD_REASON)"
        kubectl describe pod test-full-policy
        TEST5_RESULT="FAILED"
    fi
    echo ""
}

test_command_mismatch_pod() {
    log_test "TEST 6: Creating a pod with MISMATCHED COMMAND (should be REJECTED)..."
    echo ""
    
    # Load the full policy (expects command: ["/bin/myapp"])
    local policy_file="$POD_POLICIES_DIR/full-policy-pod-policy.json"
    local policy_json
    policy_json=$(load_policy_json "$policy_file")
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    # Sign the policy
    local signature
    signature=$(sign_policy "$policy_base64")
    
    if [[ -z "$signature" || "$signature" == "null" ]]; then
        log_error "Failed to sign policy"
        TEST6_RESULT="FAILED"
        return
    fi
    
    log_info "Creating pod with different command (policy expects /bin/myapp, using /bin/sh)..."
    
    # Create pod with DIFFERENT command than policy
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-command-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for admission decision..."
    sleep 5
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-command-mismatch -o wide 2>/dev/null || echo "Pod not found"
    echo ""
    
    POD_STATUS=$(kubectl get pod test-command-mismatch -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
    POD_REASON=$(kubectl get pod test-command-mismatch -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    POD_MESSAGE=$(kubectl get pod test-command-mismatch -o jsonpath='{.status.message}' 2>/dev/null || echo "")
    
    EXPECTED_MSG="command.*does not match policy"
    if [[ "$POD_STATUS" == "Failed" && "$POD_REASON" == "NodeAdmissionRejected" ]]; then
        if echo "$POD_MESSAGE" | grep -qE "$EXPECTED_MSG"; then
            log_info "✓ TEST 6 PASSED: Pod with command mismatch was REJECTED with expected message"
            echo ""
            kubectl describe pod test-command-mismatch | grep -A3 "Message:"
            TEST6_RESULT="PASSED"
        else
            log_error "✗ TEST 6 FAILED: Pod rejected but message mismatch"
            log_error "  Expected pattern: '$EXPECTED_MSG'"
            log_error "  Got: '$POD_MESSAGE'"
            TEST6_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" ]]; then
        log_warn "? TEST 6 PARTIAL: Pod is Failed but reason is '$POD_REASON'"
        TEST6_RESULT="PARTIAL"
    else
        log_error "✗ TEST 6 FAILED: Pod with command mismatch was NOT rejected (status: $POD_STATUS)"
        kubectl describe pod test-command-mismatch
        TEST6_RESULT="FAILED"
    fi
    echo ""
}

test_env_mismatch_pod() {
    log_test "TEST 7: Creating a pod with MISMATCHED ENV (should be REJECTED)..."
    echo ""
    
    # Load the full policy
    local policy_file="$POD_POLICIES_DIR/full-policy-pod-policy.json"
    local policy_json
    policy_json=$(load_policy_json "$policy_file")
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    # Sign the policy
    local signature
    signature=$(sign_policy "$policy_base64")
    
    if [[ -z "$signature" || "$signature" == "null" ]]; then
        log_error "Failed to sign policy"
        TEST7_RESULT="FAILED"
        return
    fi
    
    log_info "Creating pod with different env (policy expects APP_ENV=production, using APP_ENV=development)..."
    
    # Create pod with DIFFERENT env than policy
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-env-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for admission decision..."
    sleep 5
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-env-mismatch -o wide 2>/dev/null || echo "Pod not found"
    echo ""
    
    POD_STATUS=$(kubectl get pod test-env-mismatch -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
    POD_REASON=$(kubectl get pod test-env-mismatch -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    POD_MESSAGE=$(kubectl get pod test-env-mismatch -o jsonpath='{.status.message}' 2>/dev/null || echo "")
    
    EXPECTED_MSG="env var.*does not match policy"
    if [[ "$POD_STATUS" == "Failed" && "$POD_REASON" == "NodeAdmissionRejected" ]]; then
        if echo "$POD_MESSAGE" | grep -qE "$EXPECTED_MSG"; then
            log_info "✓ TEST 7 PASSED: Pod with env mismatch was REJECTED with expected message"
            echo ""
            kubectl describe pod test-env-mismatch | grep -A3 "Message:"
            TEST7_RESULT="PASSED"
        else
            log_error "✗ TEST 7 FAILED: Pod rejected but message mismatch"
            log_error "  Expected pattern: '$EXPECTED_MSG'"
            log_error "  Got: '$POD_MESSAGE'"
            TEST7_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" ]]; then
        log_warn "? TEST 7 PARTIAL: Pod is Failed but reason is '$POD_REASON'"
        TEST7_RESULT="PARTIAL"
    else
        log_error "✗ TEST 7 FAILED: Pod with env mismatch was NOT rejected (status: $POD_STATUS)"
        kubectl describe pod test-env-mismatch
        TEST7_RESULT="FAILED"
    fi
    echo ""
}

test_volume_mismatch_pod() {
    log_test "TEST 8: Creating a pod with MISMATCHED VOLUME MOUNT (should be REJECTED)..."
    echo ""
    
    # Load the full policy (expects mountPath: /etc/app and /data)
    local policy_file="$POD_POLICIES_DIR/full-policy-pod-policy.json"
    local policy_json
    policy_json=$(load_policy_json "$policy_file")
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    # Sign the policy
    local signature
    signature=$(sign_policy "$policy_base64")
    
    if [[ -z "$signature" || "$signature" == "null" ]]; then
        log_error "Failed to sign policy"
        TEST8_RESULT="FAILED"
        return
    fi
    
    log_info "Creating pod with different volume mount path (policy expects /etc/app, using /etc/config)..."
    
    # Create pod with DIFFERENT volume mount path than policy
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-volume-mismatch
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for admission decision..."
    sleep 5
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-volume-mismatch -o wide 2>/dev/null || echo "Pod not found"
    echo ""
    
    POD_STATUS=$(kubectl get pod test-volume-mismatch -o jsonpath='{.status.phase}' 2>/dev/null || echo "NotFound")
    POD_REASON=$(kubectl get pod test-volume-mismatch -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    POD_MESSAGE=$(kubectl get pod test-volume-mismatch -o jsonpath='{.status.message}' 2>/dev/null || echo "")
    
    EXPECTED_MSG="volume mount.*does not match policy"
    if [[ "$POD_STATUS" == "Failed" && "$POD_REASON" == "NodeAdmissionRejected" ]]; then
        if echo "$POD_MESSAGE" | grep -qE "$EXPECTED_MSG"; then
            log_info "✓ TEST 8 PASSED: Pod with volume mount mismatch was REJECTED with expected message"
            echo ""
            kubectl describe pod test-volume-mismatch | grep -A3 "Message:"
            TEST8_RESULT="PASSED"
        else
            log_error "✗ TEST 8 FAILED: Pod rejected but message mismatch"
            log_error "  Expected pattern: '$EXPECTED_MSG'"
            log_error "  Got: '$POD_MESSAGE'"
            TEST8_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" ]]; then
        log_warn "? TEST 8 PARTIAL: Pod is Failed but reason is '$POD_REASON'"
        TEST8_RESULT="PARTIAL"
    else
        log_error "✗ TEST 8 FAILED: Pod with volume mount mismatch was NOT rejected (status: $POD_STATUS)"
        kubectl describe pod test-volume-mismatch
        TEST8_RESULT="FAILED"
    fi
    echo ""
}

test_allowall_pod() {
    log_test "TEST 9: Creating a pod with ALLOWALL policy (should be ALLOWED without validation)..."
    echo ""
    
    # Create the allowall policy: ["allowall"]
    local policy_json='["allowall"]'
    
    # Base64 encode the policy
    local policy_base64
    policy_base64=$(printf '%s' "$policy_json" | base64 -w 0)
    
    log_info "Policy: $policy_json"
    log_info "Policy base64: $policy_base64"
    
    # Sign the policy
    log_info "Signing allowall policy using policy-signing-tool..."
    local signature
    signature=$(sign_policy "$policy_base64")
    
    if [[ -z "$signature" || "$signature" == "null" ]]; then
        log_error "Failed to sign policy"
        TEST9_RESULT="FAILED"
        return
    fi
    
    # Create a pod with any spec - it should be allowed because of allowall policy
    # Using a completely different image/command that wouldn't match any normal policy
    cat <<EOF | kubectl apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: test-allowall
  namespace: default
  annotations:
    api-server-proxy.io/policy: "$policy_base64"
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
EOF
    
    log_info "Waiting for pod to be scheduled..."
    sleep 10
    
    echo ""
    echo "Pod status:"
    kubectl get pod test-allowall -o wide
    echo ""
    
    # Check pod status
    POD_STATUS=$(kubectl get pod test-allowall -o jsonpath='{.status.phase}')
    POD_REASON=$(kubectl get pod test-allowall -o jsonpath='{.status.reason}' 2>/dev/null || echo "")
    POD_NODE=$(kubectl get pod test-allowall -o jsonpath='{.spec.nodeName}' 2>/dev/null || echo "")
    
    if [[ "$POD_STATUS" == "Running" || "$POD_STATUS" == "Pending" || "$POD_STATUS" == "ContainerCreating" ]]; then
        if [[ "$POD_REASON" != "NodeAdmissionRejected" ]]; then
            log_info "✓ TEST 9 PASSED: Allowall policy pod was allowed without validation (status: $POD_STATUS, node: $POD_NODE)"
            TEST9_RESULT="PASSED"
        else
            log_error "✗ TEST 9 FAILED: Allowall policy pod was rejected"
            kubectl describe pod test-allowall
            TEST9_RESULT="FAILED"
        fi
    elif [[ "$POD_STATUS" == "Failed" && "$POD_REASON" != "NodeAdmissionRejected" ]]; then
        # Pod failed for other reasons - that's OK, it was admitted
        log_info "✓ TEST 9 PASSED: Allowall policy pod was admitted (failed later due to: $POD_REASON)"
        TEST9_RESULT="PASSED"
    else
        log_error "✗ TEST 9 FAILED: Allowall policy pod status is $POD_STATUS (reason: $POD_REASON)"
        kubectl describe pod test-allowall
        TEST9_RESULT="FAILED"
    fi
    echo ""
}

run_tests() {
    echo ""
    echo "========================================"
    echo "  Pod Policy Verification Tests (AKS)"
    echo "========================================"
    echo ""
    
    check_prerequisites
    check_signing_keys
    check_proxy_status
    cleanup_test_resources
    test_signed_pod
    test_unsigned_pod
    test_bad_signature_pod
    test_image_mismatch_pod
    test_full_policy_pod
    test_command_mismatch_pod
    test_env_mismatch_pod
    test_volume_mismatch_pod
    test_allowall_pod
    
    echo ""
    echo "========================================"
    echo "  Test Results Summary"
    echo "========================================"
    echo ""
    
    # Count results
    local passed=0
    local failed=0
    local partial=0
    
    # Print individual test results
    if [[ "$TEST1_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 1: Signed pod allowed       - PASSED"
        passed=$((passed + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 1: Signed pod allowed       - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST2_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 2: Unsigned pod rejected    - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST2_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 2: Unsigned pod rejected    - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 2: Unsigned pod rejected    - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST3_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 3: Bad signature rejected   - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST3_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 3: Bad signature rejected   - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 3: Bad signature rejected   - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST4_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 4: Image mismatch rejected  - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST4_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 4: Image mismatch rejected  - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 4: Image mismatch rejected  - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST5_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 5: Full policy pod allowed  - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST5_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 5: Full policy pod allowed  - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 5: Full policy pod allowed  - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST6_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 6: Command mismatch rejected - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST6_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 6: Command mismatch rejected - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 6: Command mismatch rejected - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST7_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 7: Env mismatch rejected    - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST7_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 7: Env mismatch rejected    - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 7: Env mismatch rejected    - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST8_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 8: Volume mismatch rejected - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST8_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 8: Volume mismatch rejected - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 8: Volume mismatch rejected - FAILED"
        failed=$((failed + 1))
    fi
    
    if [[ "$TEST9_RESULT" == "PASSED" ]]; then
        echo -e "  ${GREEN}✓${NC} TEST 9: Allowall policy allowed  - PASSED"
        passed=$((passed + 1))
    elif [[ "$TEST9_RESULT" == "PARTIAL" ]]; then
        echo -e "  ${YELLOW}?${NC} TEST 9: Allowall policy allowed  - PARTIAL"
        partial=$((partial + 1))
    else
        echo -e "  ${RED}✗${NC} TEST 9: Allowall policy allowed  - FAILED"
        failed=$((failed + 1))
    fi
    
    echo ""
    echo "----------------------------------------"
    if [[ $failed -eq 0 && $partial -eq 0 ]]; then
        echo -e "  ${GREEN}All $passed tests PASSED!${NC}"
    elif [[ $failed -eq 0 ]]; then
        echo -e "  ${YELLOW}$passed passed, $partial partial${NC}"
    else
        echo -e "  ${RED}$passed passed, $failed failed, $partial partial${NC}"
    fi
    echo "----------------------------------------"
    echo ""
    echo "To clean up test resources:"
    echo "  kubectl delete pod test-signed test-unsigned test-bad-sig test-image-mismatch test-full-policy test-command-mismatch test-env-mismatch test-volume-mismatch test-allowall"
    echo ""
    
    # Exit with error if any tests failed
    if [[ $failed -gt 0 ]]; then
        exit 1
    fi
}

# Parse arguments
case "${1:-}" in
    --status)
        check_prerequisites
        check_signing_keys
        check_proxy_status
        ;;
    --cleanup)
        cleanup_test_resources
        log_info "Test resources cleaned up"
        ;;
    --help|-h)
        echo "Usage: $0 [options]"
        echo ""
        echo "Tests pod scheduling with signed pod policies on AKS."
        echo "Assumes deploy-cluster.sh, deploy-flex-node-vm.sh, and deploy-api-server-proxy.sh were run previously."
        echo ""
        echo "Options:"
        echo "  --status     Check signing keys and proxy status only"
        echo "  --cleanup    Clean up test resources only"
        echo "  --help, -h   Show this help message"
        echo ""
        exit 0
        ;;
    *)
        run_tests
        ;;
esac
