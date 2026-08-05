#!/bin/bash
# Configures the GPU container runtime and host services after containerd
# is available. This is the post-containerd layer of GPU setup.
#
# Currently implements:
#   0. GPU config parsing   — optional MPS config from FLEX_NODE_GPU_CONFIG_B64.
#   1. Container runtime    — nvidia-ctk, containerd nvidia runtime, CDI specs.
#   2. Device plugin        — nvidia-device-plugin systemd service.
#   3. GPU Feature Discovery — gpu-feature-discovery systemd service.
#  3a. GPU label reconciler — applies GFD labels to node, replacing NFD.
#   4. Metrics              — DCGM exporter + GPU health exporter.
#
# Prerequisites:
#   - NVIDIA GPU driver already installed (install-gpu-driver.sh).
#   - containerd already installed and config migrated (install-flex-node-agent.sh).
#
# Must run AFTER install-flex-node-agent.sh so that containerd is available
# and its config has been migrated to the correct format.
set -e

# Ensure python3 is available for GPU config parsing below. CVM images
# include python3, but this guard handles minimal base images.
if ! command -v python3 &>/dev/null; then
    sudo apt-get update -qq && sudo apt-get install -y -qq python3
fi

# =============================================================================
# GPU CONFIG PARSING
# Reads optional GPU config from FLEX_NODE_GPU_CONFIG_B64 env var (base64
# JSON set by FlexNodeProvider). Extracts sharing mode and MPS settings.
# Defaults to no sharing when the env var is absent.
# =============================================================================
GPU_SHARING_MODE="none"
GPU_MPS_REPLICAS=""
GPU_MPS_RENAME_BY_DEFAULT="false"

if [ -n "${FLEX_NODE_GPU_CONFIG_B64:-}" ]; then
    _gpu_json="$(printf '%s' "${FLEX_NODE_GPU_CONFIG_B64}" | base64 --decode)"
    GPU_SHARING_MODE="$(printf '%s' "${_gpu_json}" \
        | python3 -c "import sys,json; d=json.load(sys.stdin); \
            print((d.get('sharing') or {}).get('mode','none').strip().lower())")"
    GPU_MPS_REPLICAS="$(printf '%s' "${_gpu_json}" \
        | python3 -c "import sys,json; d=json.load(sys.stdin); \
            r=(d.get('sharing') or {}).get('replicas'); \
            print(r if r is not None else '')")"
    GPU_MPS_RENAME_BY_DEFAULT="$(printf '%s' "${_gpu_json}" \
        | python3 -c "import sys,json; d=json.load(sys.stdin); \
            print(str((d.get('sharing') or {}).get('renameByDefault',False)).lower())")"
    unset _gpu_json
fi

echo "GPU sharing mode: ${GPU_SHARING_MODE}"

echo "Installing NVIDIA Container Toolkit on host..."

# =============================================================================
# 1. CONTAINER RUNTIME
# Installs nvidia-container-toolkit and configures containerd to use the
# nvidia runtime with CDI device injection. Replaces GPU Operator's toolkit
# DaemonSet.
# =============================================================================

# Add the NVIDIA container toolkit apt repository and install the toolkit.
# Pin to 1.19.0-1 to match the container-toolkit v1.19.0 image used by GPU
# Operator chart v26.3.1. All four packages are pinned per NVIDIA's install
# guide to ensure consistent versions.
sudo apt-get update
sudo apt-get install -y --no-install-recommends ca-certificates curl gnupg2 python3
curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey \
  | sudo gpg --yes --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
  | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
  | sudo tee /etc/apt/sources.list.d/nvidia-container-toolkit.list > /dev/null
sudo apt-get update
NVIDIA_CTK_VERSION="1.19.0-1"
sudo apt-get install -y \
  nvidia-container-toolkit=${NVIDIA_CTK_VERSION} \
  nvidia-container-toolkit-base=${NVIDIA_CTK_VERSION} \
  libnvidia-container-tools=${NVIDIA_CTK_VERSION} \
  libnvidia-container1=${NVIDIA_CTK_VERSION}

# Configure containerd to use the nvidia runtime. This writes a drop-in config
# that registers the nvidia runtime handler and sets CDI as the device injection
# mechanism. The containerd config must already be in the correct format (v3 on
# containerd v2) — this is handled by the config migration in
# install-flex-node-agent.sh.
echo "Configuring containerd with nvidia runtime..."
sudo nvidia-ctk runtime configure --runtime=containerd
sudo systemctl restart containerd

# Generate initial CDI specs. The nvidia-container-toolkit package installs
# nvidia-cdi-refresh.service and nvidia-cdi-refresh.path which automatically
# regenerate CDI specs at /var/run/cdi/nvidia.yaml when GPU state changes
# (driver upgrade, device hotplug). Enable and start these for ongoing refresh.
echo "Generating CDI specs and enabling automatic refresh..."
sudo mkdir -p /var/run/cdi
sudo nvidia-ctk cdi generate --output=/var/run/cdi/nvidia.yaml
sudo systemctl enable --now nvidia-cdi-refresh.path

# Verify the setup.
echo "Verifying nvidia container toolkit setup..."
nvidia-ctk --version
nvidia-ctk cdi list
echo "NVIDIA Container Toolkit installed and configured successfully."

# =============================================================================
# 2. DEVICE PLUGIN
# Registers nvidia.com/gpu as a schedulable Kubernetes resource via kubelet's
# device plugin gRPC API. Replaces GPU Operator's device-plugin DaemonSet.
# =============================================================================

echo "Installing NVIDIA device plugin and GPU feature discovery..."

# Extract binaries from the pinned container image. The image is the same
# version used by GPU Operator chart v26.3.1 and contains both the device
# plugin and GPU feature discovery binaries.
GPU_PLUGIN_IMAGE="nvcr.io/nvidia/k8s-device-plugin:v0.19.0"
GPU_PLUGIN_ROOT="$(mktemp -d)"

cleanup_gpu_plugin_root() {
  sudo ctr images unmount --rm "${GPU_PLUGIN_ROOT}" > /dev/null 2>&1 || true
  sudo rm -rf "${GPU_PLUGIN_ROOT}"
}
trap cleanup_gpu_plugin_root EXIT

sudo ctr images pull "${GPU_PLUGIN_IMAGE}" --quiet
sudo ctr images mount "${GPU_PLUGIN_IMAGE}" "${GPU_PLUGIN_ROOT}" > /dev/null
sudo install -D -m 0755 \
  "${GPU_PLUGIN_ROOT}/usr/bin/nvidia-device-plugin" \
  /usr/local/bin/nvidia-device-plugin
sudo install -D -m 0755 \
  "${GPU_PLUGIN_ROOT}/usr/bin/gpu-feature-discovery" \
  /usr/local/bin/gpu-feature-discovery
if [ "${GPU_SHARING_MODE}" = "mps" ]; then
    sudo install -D -m 0755 \
      "${GPU_PLUGIN_ROOT}/usr/bin/mps-control-daemon" \
      /usr/local/bin/mps-control-daemon
fi
cleanup_gpu_plugin_root
trap - EXIT

# Write device plugin config. When MPS is enabled, includes mpsRoot and
# sharing.mps settings. The mps-control-daemon reads the same config file.
sudo mkdir -p /etc/nvidia
if [ "${GPU_SHARING_MODE}" = "mps" ]; then
    echo "Writing device plugin config with MPS sharing (replicas=${GPU_MPS_REPLICAS})..."
    sudo tee /etc/nvidia/device-plugin-config.yaml > /dev/null << CFGEOF
version: v1
flags:
  migStrategy: "none"
  gdrcopyEnabled: false
  gdsEnabled: false
  mofedEnabled: false
  mpsRoot: "/run/nvidia/mps"
sharing:
  mps:
    renameByDefault: ${GPU_MPS_RENAME_BY_DEFAULT}
    resources:
    - name: nvidia.com/gpu
      replicas: ${GPU_MPS_REPLICAS}
CFGEOF
else
    sudo tee /etc/nvidia/device-plugin-config.yaml > /dev/null << 'CFGEOF'
version: v1
flags:
  migStrategy: "none"
  gdrcopyEnabled: false
  gdsEnabled: false
  mofedEnabled: false
CFGEOF
fi

# When MPS is enabled, install and start the MPS control daemon before the
# device plugin. The daemon starts nvidia-cuda-mps-control, sets GPU compute
# mode to EXCLUSIVE_PROCESS, and configures per-device memory/thread limits
# based on the replica count. The device plugin waits for the daemon to be
# healthy before registering GPU resources with kubelet.
_dp_after="kubelet.service nvidia-persistenced.service"
_dp_wants="kubelet.service"

if [ "${GPU_SHARING_MODE}" = "mps" ]; then
    echo "Installing MPS control daemon..."
    sudo mkdir -p /run/nvidia/mps
    # The mps-control-daemon binary uses /mps as its internal root path.
    # Create a symlink so host paths resolve correctly. The symlink lives
    # outside /run so it survives reboot.
    sudo ln -sfn /run/nvidia/mps /mps

    # Write a wrapper script that bind-mounts /run/nvidia/mps/shm over
    # /dev/shm so the daemon's nvidia-cuda-mps-control uses the MPS-
    # specific shm. The wrapper runs inside `unshare --mount` (from the
    # systemd unit) so the /dev/shm bind-mount is private to the daemon
    # process tree and does not affect system-wide /dev/shm.
    #
    # The MPS shm tmpfs itself is created by ExecStartPre (mount-shm)
    # in the HOST mount namespace so that the device plugin and workload
    # pods can access /run/nvidia/mps/shm via hostPath mounts. This
    # mirrors the Helm DaemonSet's Bidirectional mountPropagation on
    # the init container.
    sudo tee /usr/local/bin/mps-control-daemon-wrapper > /dev/null << 'WRAPPER'
#!/bin/bash
set -e
mount --bind /run/nvidia/mps/shm /dev/shm
exec /usr/local/bin/mps-control-daemon "$@"
WRAPPER
    sudo chmod +x /usr/local/bin/mps-control-daemon-wrapper

    sudo tee /etc/systemd/system/nvidia-mps-control-daemon.service > /dev/null << 'MPSSVC'
[Unit]
Description=NVIDIA MPS Control Daemon
After=nvidia-persistenced.service
Wants=nvidia-persistenced.service

[Service]
Type=simple
# Create the MPS root directory and shm tmpfs in the host mount namespace
# so that /run/nvidia/mps/shm is visible to the device plugin and workload
# pods. The wrapper then bind-mounts this over /dev/shm inside a private
# mount namespace so system-wide /dev/shm is unaffected.
ExecStartPre=/bin/mkdir -p /run/nvidia/mps
ExecStartPre=/usr/local/bin/mps-control-daemon mount-shm
ExecStart=/bin/unshare --mount -- /usr/local/bin/mps-control-daemon-wrapper --config-file=/etc/nvidia/device-plugin-config.yaml
Restart=on-failure
RestartSec=10
TimeoutStopSec=20

[Install]
WantedBy=multi-user.target
MPSSVC

    sudo systemctl daemon-reload
    sudo systemctl enable --now nvidia-mps-control-daemon.service
    echo "MPS control daemon installed and started."

    _dp_after="${_dp_after} nvidia-mps-control-daemon.service"
    _dp_wants="${_dp_wants} nvidia-mps-control-daemon.service"
fi

# Create a systemd service for the device plugin. It registers nvidia.com/gpu
# resources with kubelet via the device plugin gRPC socket at
# /var/lib/kubelet/device-plugins/. The service starts after kubelet so the
# socket directory exists and kubelet is ready to accept registrations. The
# device plugin handles kubelet restarts and re-registration automatically.
# When MPS is enabled, the service also depends on the MPS control daemon.
sudo tee /etc/systemd/system/nvidia-device-plugin.service > /dev/null << SVCEOF
[Unit]
Description=NVIDIA Kubernetes Device Plugin
After=${_dp_after}
Wants=${_dp_wants}

[Service]
Type=simple
ExecStartPre=/bin/mkdir -p /var/lib/kubelet/device-plugins
ExecStart=/usr/local/bin/nvidia-device-plugin --config-file=/etc/nvidia/device-plugin-config.yaml
Restart=on-failure
RestartSec=10
TimeoutStopSec=20
Environment="KUBECONFIG="
Environment="NODE_NAME=%H"

[Install]
WantedBy=multi-user.target
SVCEOF

sudo systemctl daemon-reload
sudo systemctl enable --now nvidia-device-plugin.service

echo "NVIDIA device plugin installed and started."

# =============================================================================
# 3. GPU FEATURE DISCOVERY (GFD)
# Labels the Kubernetes node with GPU properties (model, memory, CUDA
# version) so the scheduler can match pods to GPU
# capabilities. Replaces GPU Operator's gpu-feature-discovery DaemonSet.
#
# GFD writes labels to a local file. The GPU label reconciler (3a)
# reads this file and applies the labels to the node via the kubelet's
# kubeconfig, replacing the NFD worker DaemonSet that previously
# handled file→node-label bridging.
# =============================================================================

echo "Configuring GPU feature discovery..."

# Write GFD config. When MPS is enabled, include the sharing block so GFD
# sets the nvidia.com/mps.capable=true label.
if [ "${GPU_SHARING_MODE}" = "mps" ]; then
    sudo tee /etc/nvidia/gfd-config.yaml > /dev/null << GFDEOF
version: v1
flags:
  migStrategy: "none"
  mpsRoot: "/run/nvidia/mps"
  gfd:
    noTimestamp: true
    sleepInterval: "60s"
    outputFile: "/etc/kubernetes/node-feature-discovery/features.d/gfd"
sharing:
  mps:
    renameByDefault: ${GPU_MPS_RENAME_BY_DEFAULT}
    resources:
    - name: nvidia.com/gpu
      replicas: ${GPU_MPS_REPLICAS}
GFDEOF
else
    sudo tee /etc/nvidia/gfd-config.yaml > /dev/null << 'GFDEOF'
version: v1
flags:
  migStrategy: "none"
  gfd:
    noTimestamp: true
    sleepInterval: "60s"
    outputFile: "/etc/kubernetes/node-feature-discovery/features.d/gfd"
GFDEOF
fi

sudo mkdir -p /etc/kubernetes/node-feature-discovery/features.d

_gfd_after="nvidia-persistenced.service"
_gfd_wants="nvidia-persistenced.service"

sudo tee /etc/systemd/system/gpu-feature-discovery.service > /dev/null << SVCEOF
[Unit]
Description=NVIDIA GPU Feature Discovery
After=${_gfd_after}
Wants=${_gfd_wants}

[Service]
Type=simple
ExecStartPre=/bin/mkdir -p /etc/kubernetes/node-feature-discovery/features.d
ExecStart=/usr/local/bin/gpu-feature-discovery \\
  --config-file=/etc/nvidia/gfd-config.yaml \\
  --use-node-feature-api=false
Restart=on-failure
RestartSec=30
TimeoutStopSec=20

[Install]
WantedBy=multi-user.target
SVCEOF

sudo systemctl daemon-reload
sudo systemctl enable --now gpu-feature-discovery.service

echo "GPU feature discovery installed and started."

# =============================================================================
# 3a. GPU LABEL RECONCILER
# Reads GFD's file-based label output and applies them as Kubernetes
# node labels via the Kubernetes API using the kubelet's kubeconfig
# credentials directly (no kubectl dependency). Replaces the NFD
# worker DaemonSet that previously bridged GFD file output to labels.
#
# Auto-detects the kubelet kubeconfig path (AKS Flex, standard AKS,
# Kind). Handles exec credential provider (AKS Flex) and client
# certificate auth (Kind). Works before and after api-server-proxy
# replaces the kubeconfig.
#
# Tracks previously applied labels in a local state file so it can
# prune stale labels without touching GPU Operator control labels
# (nvidia.com/gpu.deploy.*, nvidia.com/gpu.present). Fails closed
# when the GFD file is absent or invalid — removes only reconciler-
# owned labels to prevent scheduling GPU pods to a broken node.
#
# Runs as a systemd timer (60s interval) to match GFD's sleepInterval.
# =============================================================================

echo "Installing GPU label reconciler..."

sudo tee /usr/local/bin/gpu-label-reconciler > /dev/null << 'PYEOF'
#!/usr/bin/env python3
"""GPU label reconciler — applies GFD labels to the Kubernetes node.

Reads GFD's file-based label output and applies them as node labels
via the Kubernetes API using the kubelet's kubeconfig credentials
directly. No kubectl dependency required. Replaces the NFD worker
DaemonSet. Tracks previously applied labels to enable pruning of
stale labels without touching GPU Operator control labels.
"""
import base64
import json
import os
import re
import ssl
import subprocess
import sys
import urllib.error
import urllib.request

GFD_LABEL_FILE = (
    "/etc/kubernetes/node-feature-discovery/features.d/gfd"
)
STATE_DIR = "/var/lib/gpu-label-reconciler"
STATE_FILE = os.path.join(STATE_DIR, "last-applied-labels")
FAIL_COUNT_FILE = os.path.join(
    STATE_DIR, "consecutive-failures"
)
FAIL_THRESHOLD = 3

# Kubeconfig paths in order of preference.
KUBECONFIG_PATHS = [
    "/var/lib/kubelet/kubelet/kubeconfig",  # AKS Flex
    "/var/lib/kubelet/kubeconfig",           # Standard AKS
    "/etc/kubernetes/kubelet.conf",           # Kind
]

# Matches key=value or bare key (defaults to "true" per NFD
# local-source spec).
LABEL_PATTERN = re.compile(
    r"^[a-zA-Z0-9][a-zA-Z0-9_./-]*(=[a-zA-Z0-9_.+-]*)?$"
)


def get_node_name():
    """Return hostname as the Kubernetes node name.

    On AKS Flex the VM ComputerName is set to the node name.
    On Kind the container hostname matches the node name.
    """
    return os.uname().nodename


def find_kubeconfig():
    """Find the kubelet kubeconfig at a known path."""
    for path in KUBECONFIG_PATHS:
        if os.path.exists(path):
            return path
    return None


def parse_kubeconfig(path):
    """Parse kubeconfig for server, CA, and auth credentials.

    Uses regex on the raw text — no YAML library required.
    Handles exec credential provider (AKS Flex) and client
    certificate auth (Kind).
    """
    with open(path) as f:
        text = f.read()

    config = {}

    m = re.search(
        r"^\s*server:\s*(\S+)", text, re.MULTILINE
    )
    if m:
        config["server"] = m.group(1)

    m = re.search(
        r"^\s*certificate-authority-data:\s*(\S+)",
        text, re.MULTILINE,
    )
    if m:
        config["ca_data"] = m.group(1)
    m = re.search(
        r"^\s*certificate-authority:\s*(\S+)",
        text, re.MULTILINE,
    )
    if m:
        config["ca_file"] = m.group(1)

    m = re.search(
        r"^\s*client-certificate:\s*(\S+)",
        text, re.MULTILINE,
    )
    if m:
        config["client_cert"] = m.group(1)
    m = re.search(
        r"^\s*client-certificate-data:\s*(\S+)",
        text, re.MULTILINE,
    )
    if m:
        config["client_cert_data"] = m.group(1)
    m = re.search(
        r"^\s*client-key:\s*(\S+)", text, re.MULTILINE
    )
    if m:
        config["client_key"] = m.group(1)
    m = re.search(
        r"^\s*client-key-data:\s*(\S+)",
        text, re.MULTILINE,
    )
    if m:
        config["client_key_data"] = m.group(1)

    # Exec credential provider block
    exec_match = re.search(
        r"^\s*exec:\s*\n((?:^\s+.*\n)*)",
        text, re.MULTILINE,
    )
    if exec_match:
        exec_block = exec_match.group(1)
        m = re.search(r"command:\s*(\S+)", exec_block)
        if m:
            config["exec_command"] = m.group(1)
            args = []
            args_match = re.search(
                r"args:\s*\n((?:\s*-\s+.*\n)*)",
                exec_block,
            )
            if args_match:
                for am in re.finditer(
                    r"-\s+(.*)", args_match.group(1)
                ):
                    arg = am.group(1).strip()
                    if (
                        len(arg) >= 2
                        and arg[0] in ('"', "'")
                        and arg[-1] == arg[0]
                    ):
                        arg = arg[1:-1]
                    args.append(arg)
            config["exec_args"] = args
            env_vars = []
            env_match = re.search(
                r"env:\s*\n((?:\s+.*\n)*)",
                exec_block,
            )
            if env_match:
                for pair in re.finditer(
                    r"name:\s*(\S+)\s+value:\s*(\S+)",
                    env_match.group(1),
                ):
                    env_vars.append(
                        (pair.group(1), pair.group(2))
                    )
            if env_vars:
                config["exec_env"] = env_vars

    return config


def get_bearer_token(config):
    """Get bearer token from exec credential provider."""
    cmd = config.get("exec_command")
    if not cmd:
        return None
    args = [cmd] + config.get("exec_args", [])
    env = None
    exec_env = config.get("exec_env")
    if exec_env:
        env = os.environ.copy()
        for name, value in exec_env:
            env[name] = value
    try:
        result = subprocess.run(
            args, env=env,
            capture_output=True, text=True, timeout=30,
        )
        if result.returncode != 0:
            print(
                f"ERROR: Exec credential failed: "
                f"{result.stderr.strip()[:200]}",
                file=sys.stderr,
            )
            return None
        cred = json.loads(result.stdout)
        return cred.get("status", {}).get("token")
    except (
        subprocess.TimeoutExpired,
        json.JSONDecodeError,
        FileNotFoundError,
    ) as e:
        print(
            f"ERROR: Exec credential error: {e}",
            file=sys.stderr,
        )
        return None


def create_ssl_context(config):
    """Create SSL context from kubeconfig CA and client certs."""
    ctx = ssl.create_default_context()

    ca_data = config.get("ca_data")
    ca_file = config.get("ca_file")
    if ca_data:
        ca_pem = base64.b64decode(ca_data).decode()
        ctx.load_verify_locations(cadata=ca_pem)
    elif ca_file and os.path.exists(ca_file):
        ctx.load_verify_locations(ca_file)

    # Client certificate auth (Kind environment)
    cert_data = config.get("client_cert_data")
    key_data = config.get("client_key_data")
    cert_file = config.get("client_cert")
    key_file = config.get("client_key")
    if cert_data and key_data:
        os.makedirs(STATE_DIR, exist_ok=True)
        ct = os.path.join(STATE_DIR, "client.crt")
        kt = os.path.join(STATE_DIR, "client.key")
        with open(ct, "wb") as f:
            f.write(base64.b64decode(cert_data))
        with open(kt, "wb") as f:
            f.write(base64.b64decode(key_data))
        os.chmod(kt, 0o600)
        ctx.load_cert_chain(ct, kt)
    elif cert_file and key_file:
        ctx.load_cert_chain(cert_file, key_file)

    return ctx


def patch_node_labels(
    server, node_name, labels_to_set, labels_to_remove,
    ssl_ctx, bearer_token=None,
):
    """PATCH node labels via the Kubernetes API."""
    if not labels_to_set and not labels_to_remove:
        return True

    url = f"{server}/api/v1/nodes/{node_name}"
    label_patch = {}
    for key, value in labels_to_set.items():
        label_patch[key] = value
    for key in labels_to_remove:
        label_patch[key] = None
    body = json.dumps(
        {"metadata": {"labels": label_patch}}
    ).encode()

    req = urllib.request.Request(
        url, data=body, method="PATCH",
        headers={
            "Content-Type":
                "application/merge-patch+json",
            "Accept": "application/json",
        },
    )
    if bearer_token:
        req.add_header(
            "Authorization", f"Bearer {bearer_token}"
        )

    try:
        with urllib.request.urlopen(
            req, context=ssl_ctx, timeout=30,
        ) as resp:
            return resp.status == 200
    except urllib.error.HTTPError as e:
        err = e.read().decode(errors="replace")[:200]
        print(
            f"ERROR: API PATCH failed ({e.code}): {err}",
            file=sys.stderr,
        )
        return False
    except (urllib.error.URLError, OSError) as e:
        print(
            f"ERROR: API connection failed: {e}",
            file=sys.stderr,
        )
        return False


def read_gfd_labels():
    """Parse GFD output file into a dict of labels.

    Supports key=value and bare key (value defaults to "true"
    per NFD local-source spec). Returns None if the file is
    absent, empty, or contains malformed lines.
    """
    if not os.path.exists(GFD_LABEL_FILE):
        return None
    labels = {}
    with open(GFD_LABEL_FILE) as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            if not LABEL_PATTERN.match(line):
                print(
                    f"ERROR: Malformed label: {line!r}",
                    file=sys.stderr,
                )
                return None
            if "=" in line:
                key, value = line.split("=", 1)
            else:
                key, value = line, "true"
            labels[key] = value
    if not labels:
        return None
    return labels


def read_previous_labels():
    """Read label keys previously applied by this reconciler."""
    if not os.path.exists(STATE_FILE):
        return set()
    with open(STATE_FILE) as f:
        return {l.strip() for l in f if l.strip()}


def write_current_labels(label_keys):
    """Persist label keys applied in this run."""
    os.makedirs(STATE_DIR, exist_ok=True)
    with open(STATE_FILE, "w") as f:
        for key in sorted(label_keys):
            f.write(key + "\n")


def read_fail_count():
    """Read the consecutive failure count."""
    if not os.path.exists(FAIL_COUNT_FILE):
        return 0
    try:
        with open(FAIL_COUNT_FILE) as f:
            return int(f.read().strip())
    except (ValueError, OSError):
        return 0


def write_fail_count(count):
    """Write the consecutive failure count."""
    os.makedirs(STATE_DIR, exist_ok=True)
    with open(FAIL_COUNT_FILE, "w") as f:
        f.write(str(count) + "\n")


def main():
    kubeconfig_path = find_kubeconfig()
    if not kubeconfig_path:
        print(
            "ERROR: No kubelet kubeconfig found at "
            + ", ".join(KUBECONFIG_PATHS),
            file=sys.stderr,
        )
        return

    config = parse_kubeconfig(kubeconfig_path)
    server = config.get("server")
    if not server:
        print(
            "ERROR: No server URL in kubeconfig",
            file=sys.stderr,
        )
        return

    ssl_ctx = create_ssl_context(config)
    bearer_token = get_bearer_token(config)

    node_name = get_node_name()
    previous_keys = read_previous_labels()
    current_labels = read_gfd_labels()

    if current_labels is None:
        fail_count = read_fail_count() + 1
        write_fail_count(fail_count)
        if fail_count < FAIL_THRESHOLD:
            print(
                f"GFD file unavailable (attempt "
                f"{fail_count}/{FAIL_THRESHOLD})"
            )
            return
        if previous_keys:
            print(
                f"GFD unavailable for {fail_count} checks"
                f" — removing {len(previous_keys)} labels"
            )
            ok = patch_node_labels(
                server, node_name, {},
                previous_keys, ssl_ctx, bearer_token,
            )
            if ok:
                write_current_labels(set())
        else:
            print(
                "GFD unavailable, no previous labels"
            )
        return

    write_fail_count(0)
    stale = previous_keys - set(current_labels.keys())
    ok = patch_node_labels(
        server, node_name, current_labels,
        stale, ssl_ctx, bearer_token,
    )
    if ok:
        write_current_labels(set(current_labels.keys()))
        print(
            f"Reconciled {len(current_labels)} labels, "
            f"pruned {len(stale)} stale"
        )
    else:
        print(
            "Reconciliation failed — retry next cycle",
            file=sys.stderr,
        )


if __name__ == "__main__":
    main()
PYEOF

sudo chmod 0755 /usr/local/bin/gpu-label-reconciler

# Systemd oneshot service for the reconciler.
sudo tee /etc/systemd/system/gpu-label-reconciler.service > /dev/null << 'SVCEOF'
[Unit]
Description=GPU Label Reconciler
After=gpu-feature-discovery.service kubelet.service
Wants=gpu-feature-discovery.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/gpu-label-reconciler
TimeoutStartSec=60
SVCEOF

# Systemd timer — runs every 60s to match GFD's sleepInterval.
sudo tee /etc/systemd/system/gpu-label-reconciler.timer > /dev/null << 'TMREOF'
[Unit]
Description=GPU Label Reconciler Timer

[Timer]
OnBootSec=30s
OnUnitActiveSec=60s
AccuracySec=5s

[Install]
WantedBy=timers.target
TMREOF

sudo systemctl daemon-reload
sudo systemctl enable --now gpu-label-reconciler.timer

echo "GPU label reconciler installed and started."

# =============================================================================
# 4. GPU METRICS AND HEALTH
# =============================================================================

# -----------------------------------------------------------------------------
# 4a. DCGM EXPORTER
# Exports GPU hardware metrics (utilization, temperature, memory, power,
# ECC errors) in Prometheus format on :9400. Replaces GPU Operator's
# nvidia-dcgm-exporter DaemonSet.
#
# The dcgm-exporter binary is a Go wrapper that dlopen()s libdcgm.so.4
# at runtime. Both the binary and the DCGM shared libraries must be
# extracted from the container image. The -k flag enables per-pod GPU
# attribution via kubelet's PodResources gRPC socket (local, no API
# server access needed).
# -----------------------------------------------------------------------------

echo "Installing DCGM exporter..."

DCGM_IMAGE="nvcr.io/nvidia/k8s/dcgm-exporter:4.5.1-4.8.0-distroless"
DCGM_ROOT="$(mktemp -d)"

cleanup_dcgm_root() {
  sudo ctr images unmount --rm "${DCGM_ROOT}" > /dev/null 2>&1 || true
  sudo rm -rf "${DCGM_ROOT}"
}
trap cleanup_dcgm_root EXIT

sudo ctr images pull "${DCGM_IMAGE}" --quiet
sudo ctr images mount "${DCGM_IMAGE}" "${DCGM_ROOT}" > /dev/null
sudo install -D -m 0755 \
  "${DCGM_ROOT}/usr/bin/dcgm-exporter" \
  /usr/local/bin/dcgm-exporter
sudo mkdir -p /etc/dcgm-exporter
sudo cp "${DCGM_ROOT}/etc/dcgm-exporter/default-counters.csv" \
  /etc/dcgm-exporter/default-counters.csv

# Copy DCGM shared libraries required at runtime via dlopen.
sudo cp "${DCGM_ROOT}"/usr/lib/x86_64-linux-gnu/libdcgm*.so* \
  /usr/lib/x86_64-linux-gnu/
sudo ldconfig

cleanup_dcgm_root
trap - EXIT

sudo tee /etc/systemd/system/dcgm-exporter.service > /dev/null << 'SVCEOF'
[Unit]
Description=NVIDIA DCGM Exporter
After=nvidia-persistenced.service kubelet.service
Wants=nvidia-persistenced.service kubelet.service

[Service]
Type=simple
ExecStart=/usr/local/bin/dcgm-exporter \
  --address :9400 \
  --collectors /etc/dcgm-exporter/default-counters.csv \
  -k
Restart=on-failure
RestartSec=30
TimeoutStopSec=20
Environment="DCGM_POD_RESOURCES_KUBELET_SOCKET=/var/lib/kubelet/pod-resources/kubelet.sock"

[Install]
WantedBy=multi-user.target
SVCEOF

sudo systemctl daemon-reload
sudo systemctl enable --now dcgm-exporter.service

echo "DCGM exporter installed and started."

# -----------------------------------------------------------------------------
# 4b. GPU HEALTH EXPORTER
# Lightweight replacement for GPU Operator's nvidia-node-status-exporter.
# Exports GPU stack readiness metrics on :8000/metrics in Prometheus format.
# Metric names are renamed from gpu_operator_node_* to gpu_node_* (the
# Grafana dashboard was updated to match).
#
# Unlike the original, this runs entirely on the host with no Kubernetes
# API server access and no dependency on the GPU Operator validator.
# All checks are host-native:
# - Driver: /sys/module/nvidia/version
# - Toolkit: nvidia-ctk --version
# - CUDA: nvidia-smi -L (boolean readiness)
# - Plugin: kubelet device-plugin checkpoint file for registered
#   nvidia.com/* resources
# - PCI: sysfs vendor ID scan (no lspci dependency)
# -----------------------------------------------------------------------------

echo "Installing GPU health exporter..."

sudo tee /usr/local/bin/gpu-health-exporter > /dev/null << 'PYEOF'
#!/usr/bin/env python3
"""GPU health exporter — serves Prometheus metrics on :8000/metrics.

Replaces GPU Operator's node-status-exporter with host-native checks.
No Kubernetes API access or GPU Operator validator dependency needed.
All checks are local to the host. Metric names renamed from
gpu_operator_node_* to gpu_node_* (Grafana dashboard updated to match).

Metrics emitted:
- gpu_node_metrics_ready_ts_seconds — exporter launch timestamp
- gpu_node_driver_ready — NVIDIA kernel module loaded
- gpu_node_toolkit_ready — nvidia-ctk available
- gpu_node_cuda_ready — nvidia-smi functional
- gpu_node_plugin_ready — device plugin registered with kubelet
- gpu_node_driver_validation — nvidia-smi can query GPU UUIDs
- gpu_node_driver_validation_last_success_ts_seconds
- gpu_node_device_plugin_devices_total — advertised GPU resources
- gpu_node_device_plugin_validation_last_success_ts_seconds
- gpu_node_nvidia_pci_devices_total — NVIDIA PCI devices via sysfs
"""

import http.server
import json
import os
import subprocess
import threading
import time

METRICS_PORT = 8000
CACHE_TTL = 30
NVIDIA_PCI_VENDOR = "0x10de"
KUBELET_CHECKPOINT = (
    "/var/lib/kubelet/device-plugins/kubelet_internal_checkpoint"
)

_start_ts = int(time.time())
_cache = {"metrics": "", "timestamp": 0}
_lock = threading.Lock()
_driver_validation_last_success = 0
_plugin_validation_last_success = 0


def _check_driver():
    """Check if the NVIDIA kernel module is loaded."""
    try:
        with open("/sys/module/nvidia/version") as f:
            f.read()
            return 1
    except (FileNotFoundError, PermissionError):
        return 0


def _check_toolkit():
    """Check if nvidia-ctk is available and functional."""
    try:
        r = subprocess.run(
            ["nvidia-ctk", "--version"],
            capture_output=True,
            timeout=5,
        )
        return 1 if r.returncode == 0 else 0
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return 0


def _check_cuda():
    """Check if nvidia-smi is functional (boolean readiness)."""
    try:
        r = subprocess.run(
            ["nvidia-smi", "-L"],
            capture_output=True,
            timeout=10,
        )
        return 1 if r.returncode == 0 else 0
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return 0


def _check_device_plugin():
    """Check if device plugin has registered with kubelet.

    Reads kubelet's device-plugin checkpoint file to verify that
    nvidia.com/gpu resources are registered.
    Returns (ready, device_count).
    """
    try:
        with open(KUBELET_CHECKPOINT) as f:
            data = json.load(f)
        registered = data.get("Data", {}).get(
            "RegisteredDevices", {}
        )
        count = 0
        for resource, devices in registered.items():
            if resource.startswith("nvidia.com/"):
                count += len(devices)
        return (1 if count > 0 else 0), count
    except (
        FileNotFoundError,
        PermissionError,
        json.JSONDecodeError,
        KeyError,
        TypeError,
    ):
        return 0, -1


def _check_driver_validation():
    """Active driver validation — query GPU UUIDs via nvidia-smi."""
    try:
        r = subprocess.run(
            ["nvidia-smi", "--query-gpu=uuid",
             "--format=csv,noheader"],
            capture_output=True,
            text=True,
            timeout=10,
        )
        return 1 if r.returncode == 0 and r.stdout.strip() else 0
    except (FileNotFoundError, subprocess.TimeoutExpired):
        return 0


def _count_nvidia_pci():
    """Count NVIDIA PCI devices via sysfs (no lspci needed)."""
    count = 0
    pci_dir = "/sys/bus/pci/devices"
    try:
        for dev in os.listdir(pci_dir):
            vendor_path = os.path.join(pci_dir, dev, "vendor")
            try:
                with open(vendor_path) as f:
                    if f.read().strip() == NVIDIA_PCI_VENDOR:
                        count += 1
            except (FileNotFoundError, PermissionError):
                continue
        return count
    except FileNotFoundError:
        return -1


def _generate_metrics():
    global _driver_validation_last_success
    global _plugin_validation_last_success
    now = int(time.time())
    lines = []

    lines.append(
        "# HELP gpu_node_metrics_ready_ts_seconds"
        " Timestamp of exporter launch"
    )
    lines.append("# TYPE gpu_node_metrics_ready_ts_seconds gauge")
    lines.append(f"gpu_node_metrics_ready_ts_seconds {_start_ts}")

    driver_ok = _check_driver()
    lines.append(
        "# HELP gpu_node_driver_ready"
        " NVIDIA driver is loaded (1=ready)"
    )
    lines.append("# TYPE gpu_node_driver_ready gauge")
    lines.append(f"gpu_node_driver_ready {driver_ok}")

    toolkit_ok = _check_toolkit()
    lines.append(
        "# HELP gpu_node_toolkit_ready"
        " Container toolkit is available (1=ready)"
    )
    lines.append("# TYPE gpu_node_toolkit_ready gauge")
    lines.append(f"gpu_node_toolkit_ready {toolkit_ok}")

    cuda_ok = _check_cuda()
    lines.append(
        "# HELP gpu_node_cuda_ready"
        " CUDA is functional (1=ready)"
    )
    lines.append("# TYPE gpu_node_cuda_ready gauge")
    lines.append(f"gpu_node_cuda_ready {cuda_ok}")

    plugin_ok, device_count = _check_device_plugin()
    lines.append(
        "# HELP gpu_node_plugin_ready"
        " Device plugin registered with kubelet (1=ready)"
    )
    lines.append("# TYPE gpu_node_plugin_ready gauge")
    lines.append(f"gpu_node_plugin_ready {plugin_ok}")

    driver_valid = _check_driver_validation()
    if driver_valid:
        _driver_validation_last_success = now
    lines.append(
        "# HELP gpu_node_driver_validation"
        " 1 if driver validation test passed"
    )
    lines.append("# TYPE gpu_node_driver_validation gauge")
    lines.append(f"gpu_node_driver_validation {driver_valid}")

    lines.append(
        "# HELP gpu_node_driver_validation_last_success_ts_seconds"
        " Timestamp of last successful driver validation"
    )
    lines.append(
        "# TYPE"
        " gpu_node_driver_validation_last_success_ts_seconds gauge"
    )
    lines.append(
        "gpu_node_driver_validation_last_success_ts_seconds"
        f" {_driver_validation_last_success}"
    )

    lines.append(
        "# HELP gpu_node_device_plugin_devices_total"
        " Number of GPU resources registered with kubelet"
    )
    lines.append(
        "# TYPE gpu_node_device_plugin_devices_total gauge"
    )
    lines.append(
        f"gpu_node_device_plugin_devices_total {device_count}"
    )

    if device_count > 0:
        _plugin_validation_last_success = now
    lines.append(
        "# HELP"
        " gpu_node_device_plugin_validation_last_success_ts_seconds"
        " Timestamp of last time GPU resources were registered"
    )
    lines.append(
        "# TYPE"
        " gpu_node_device_plugin_validation_last_success_ts_seconds"
        " gauge"
    )
    lines.append(
        "gpu_node_device_plugin_validation_last_success_ts_seconds"
        f" {_plugin_validation_last_success}"
    )

    pci_count = _count_nvidia_pci()
    lines.append(
        "# HELP gpu_node_nvidia_pci_devices_total"
        " Number of NVIDIA PCI devices"
    )
    lines.append(
        "# TYPE gpu_node_nvidia_pci_devices_total gauge"
    )
    lines.append(
        f"gpu_node_nvidia_pci_devices_total {pci_count}"
    )

    return "\n".join(lines) + "\n"


def _get_cached_metrics():
    now = time.time()
    with _lock:
        if now - _cache["timestamp"] > CACHE_TTL:
            _cache["metrics"] = _generate_metrics()
            _cache["timestamp"] = now
        return _cache["metrics"]


class _Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/metrics":
            body = _get_cached_metrics().encode()
            self.send_response(200)
            self.send_header(
                "Content-Type", "text/plain; charset=utf-8"
            )
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_response(404)
            self.end_headers()

    def log_message(self, fmt, *args):
        pass


if __name__ == "__main__":
    server = http.server.HTTPServer(("", METRICS_PORT), _Handler)
    server.serve_forever()
PYEOF

sudo chmod +x /usr/local/bin/gpu-health-exporter

sudo tee /etc/systemd/system/gpu-health-exporter.service > /dev/null << 'SVCEOF'
[Unit]
Description=GPU Health Exporter (Prometheus metrics)
After=nvidia-persistenced.service nvidia-device-plugin.service

[Service]
Type=simple
ExecStart=/usr/bin/python3 /usr/local/bin/gpu-health-exporter
Restart=on-failure
RestartSec=30
TimeoutStopSec=10

[Install]
WantedBy=multi-user.target
SVCEOF

sudo systemctl daemon-reload
sudo systemctl enable --now gpu-health-exporter.service

echo "GPU health exporter installed and started."

echo "GPU runtime setup completed successfully."
