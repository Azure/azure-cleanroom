#!/usr/bin/env python3
"""
KServe Inferencing Test Script

This script runs the KServe inferencing scenario tests on a pre-deployed
cleanroom environment. It expects a deployment-config.json file to be present
in the output directory.
"""

import argparse
import atexit
import base64
import json
import shutil
import subprocess
import sys
import tempfile
import time
import uuid
import webbrowser
from pathlib import Path

_git_root = subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
).stdout.strip()
sys.path.insert(0, str(Path(_git_root) / "test" / "onebox"))
from cleanroom_test_utils import (
    Colors,
    check_missing_files,
    print_missing_files_error,
    run_command,
    tail_telemetry_output,
)


def load_deployment_config(config_dir: str) -> dict:
    """Load deployment configuration from the environment setup"""
    config_file = Path(config_dir) / "deployment-config.json"
    if not config_file.exists():
        print(
            f"{Colors.RED}Deployment configuration not found at {config_file}{Colors.RESET}"
        )
        print(
            f"{Colors.YELLOW}Please run setup-env.ps1 first to setup the environment{Colors.RESET}"
        )
        sys.exit(1)

    with open(config_file, "r") as f:
        return json.load(f)


# Model name constants matching deploy-models.py.
_MODEL_NAMES = {
    "iris": "hello-iris-1",
    "tinyllama": "hello-tinyllama-1",
    "tinyllama-gpu": "hello-tinyllama-gpu-1",
    "gemma4-gpu": "hello-gemma4-gpu-1",
}
_DEFAULT_MODELS = {"iris", "tinyllama"}


def _get_expected_model_names(models_arg: str) -> list[str]:
    """Return the list of model names expected to be deployed."""
    enabled = set(m.strip() for m in models_arg.split(","))
    if "default" in enabled:
        enabled = (enabled - {"default"}) | _DEFAULT_MODELS
    return [_MODEL_NAMES[m] for m in sorted(enabled) if m in _MODEL_NAMES]


def _validate_ledger_events(
    contract_id: str,
    governance_client: str,
    expected_models: list[str],
    max_retries: int = 5,
    retry_delay: int = 5,
) -> None:
    """Validate that deployment audit events exist in the governance ledger.

    The agent emits a message of the form:
        Starting inference service deployment for '<name>' bound to
        model document '<model-doc-id>'.
    The model document id is generated at runtime so we match on the
    deployment-name-bearing prefix instead of the full string.
    """
    expected_prefixes = {
        name: f"Starting inference service deployment for '{name}'"
        for name in expected_models
    }

    missing: dict[str, str] = {}
    for attempt in range(1, max_retries + 1):
        result = run_command(
            f"az cleanroom governance contract event list "
            f"--contract-id {contract_id} "
            f"--all "
            f"--governance-client {governance_client}"
        )
        events = json.loads(result.stdout)
        event_messages = [
            e.get("data", {}).get("message", "") for e in events.get("value", [])
        ]

        missing = {
            name: prefix
            for name, prefix in expected_prefixes.items()
            if not any(prefix in msg for msg in event_messages)
        }
        if not missing:
            print(
                f"{Colors.GREEN}✅ All expected ledger events found "
                f"({len(expected_prefixes)} events).{Colors.RESET}"
            )
            return

        if attempt < max_retries:
            print(
                f"{Colors.YELLOW}Attempt {attempt}/{max_retries}: "
                f"{len(missing)} event(s) not yet in ledger, "
                f"retrying in {retry_delay}s...{Colors.RESET}"
            )
            time.sleep(retry_delay)

    print(f"{Colors.RED}Ledger event validation failed.{Colors.RESET}")
    for name, prefix in sorted(missing.items()):
        print(f"  Missing: deployment audit event for '{name}' (prefix: {prefix})")
    sys.exit(1)


GRAFANA_PORT = 3000
GRAFANA_NAMESPACE = "observability"
GRAFANA_SECRET = "cleanroom-grafana"
GRAFANA_INFERENCING_DASHBOARD_UID = "cleanroom-inferencing-metrics"

_grafana_port_forward_process: subprocess.Popen | None = None


def cleanup_grafana_port_forward() -> None:
    """Cleanup function to terminate Grafana port-forward on exit."""
    global _grafana_port_forward_process
    if _grafana_port_forward_process:
        print(f"\n{Colors.YELLOW}Cleaning up Grafana port-forward...{Colors.RESET}")
        try:
            _grafana_port_forward_process.terminate()
            _grafana_port_forward_process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            _grafana_port_forward_process.kill()
            _grafana_port_forward_process.wait()
        except Exception:
            pass
        finally:
            _grafana_port_forward_process = None


_grafana_cleanup_registered = False


def _install_inferencing_dashboard(kube_config: str) -> bool:
    """Install the inferencing dashboard ConfigMap into the cluster.
    Uses the same idempotent pattern as
    Kubectl.InstallGrafanaDashboards (create --dry-run=client | apply).
    Returns True on success, False on failure."""
    dashboard_file = (
        Path(_git_root)
        / "src"
        / "cleanroom-cluster"
        / "cleanroom-cluster-provider-client"
        / "observability"
        / "grafana"
        / "dashboards"
        / "cleanroom-inferencing-dashboard.json"
    )
    if not dashboard_file.exists():
        print(
            f"{Colors.YELLOW}Inferencing dashboard JSON not found at "
            f"{dashboard_file}, skipping install.{Colors.RESET}"
        )
        return False

    configmap_name = "cleanroom-inferencing-dashboard"
    kc = f"--kubeconfig={kube_config}"

    # Create ConfigMap (idempotent via dry-run + apply).
    create = subprocess.run(
        [
            "kubectl",
            "create",
            "configmap",
            configmap_name,
            f"--from-file={dashboard_file}",
            "-n",
            GRAFANA_NAMESPACE,
            kc,
            "--dry-run=client",
            "-o",
            "yaml",
        ],
        capture_output=True,
        text=True,
    )
    if create.returncode != 0:
        print(
            f"{Colors.YELLOW}Failed to create dashboard configmap: "
            f"{create.stderr}{Colors.RESET}"
        )
        return False

    apply = subprocess.run(
        ["kubectl", "apply", "-f", "-", "-n", GRAFANA_NAMESPACE, kc],
        input=create.stdout,
        capture_output=True,
        text=True,
    )
    if apply.returncode != 0:
        print(
            f"{Colors.YELLOW}Failed to apply dashboard configmap: "
            f"{apply.stderr}{Colors.RESET}"
        )
        return False

    # Label so Grafana sidecar discovers it.
    label = subprocess.run(
        [
            "kubectl",
            "label",
            "configmap",
            configmap_name,
            "grafana_dashboard=1",
            "-n",
            GRAFANA_NAMESPACE,
            kc,
            "--overwrite",
        ],
        capture_output=True,
        text=True,
    )
    if label.returncode != 0:
        print(
            f"{Colors.YELLOW}Failed to label dashboard configmap: "
            f"{label.stderr}{Colors.RESET}"
        )
        return False

    print(
        f"{Colors.GREEN}Inferencing dashboard installed in "
        f"{GRAFANA_NAMESPACE} namespace.{Colors.RESET}"
    )
    return True


def open_grafana(kube_config: str) -> None:
    """Start kubectl port-forward for Grafana and open the Cleanroom
    Inferencing dashboard in Edge InPrivate with auto-login."""
    global _grafana_port_forward_process, _grafana_cleanup_registered

    if not _grafana_cleanup_registered:
        atexit.register(cleanup_grafana_port_forward)
        _grafana_cleanup_registered = True

    # Ensure the dashboard ConfigMap exists in the cluster.
    if not _install_inferencing_dashboard(kube_config):
        print(
            f"{Colors.YELLOW}Dashboard install failed; opening Prometheus "
            f"Explore instead.{Colors.RESET}"
        )
        dashboard_path_fallback = "/explore?orgId=1"
    else:
        dashboard_path_fallback = None

    # Fetch admin password from K8s secret.
    result = subprocess.run(
        [
            "kubectl",
            "get",
            "secret",
            "-n",
            GRAFANA_NAMESPACE,
            GRAFANA_SECRET,
            "-o",
            "jsonpath={.data.admin-password}",
            "--kubeconfig",
            kube_config,
        ],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0 or not result.stdout:
        print(f"{Colors.RED}Failed to fetch Grafana password.{Colors.RESET}")
        return

    password = base64.b64decode(result.stdout).decode("utf-8")

    # Start port-forward in the background. Stderr goes to a temp file
    # so startup errors can be read without risk of a pipe buffer filling
    # during long-lived operation.
    _pf_stderr_file = tempfile.TemporaryFile()
    _grafana_port_forward_process = subprocess.Popen(
        [
            "kubectl",
            "port-forward",
            "-n",
            GRAFANA_NAMESPACE,
            f"svc/{GRAFANA_SECRET}",
            f"{GRAFANA_PORT}:80",
            "--kubeconfig",
            kube_config,
        ],
        stdout=subprocess.DEVNULL,
        stderr=_pf_stderr_file,
    )

    # Wait briefly for port-forward to establish.
    time.sleep(3)
    if _grafana_port_forward_process.poll() is not None:
        _pf_stderr_file.seek(0)
        stderr = _pf_stderr_file.read().decode("utf-8").strip()
        _pf_stderr_file.close()
        msg = "Grafana port-forward exited unexpectedly."
        if stderr:
            msg += f"\n  {stderr}"
        print(f"{Colors.RED}{msg}{Colors.RESET}")
        _grafana_port_forward_process = None
        return

    # Startup succeeded; close the temp file (no longer needed).
    _pf_stderr_file.close()

    dashboard_path = dashboard_path_fallback or (
        f"/d/{GRAFANA_INFERENCING_DASHBOARD_UID}"
        f"/cleanroom-inferencing?orgId=1&refresh=10s"
    )

    display_url = f"http://localhost:{GRAFANA_PORT}{dashboard_path}"

    # Chromium-based browsers (Edge, Chrome) strip user:password from URLs,
    # so auto-login via URL credentials is not possible. Print credentials
    # and open the dashboard URL directly.
    print(
        f"{Colors.CYAN}Grafana credentials — "
        f"user: admin  password: {password}{Colors.RESET}"
    )

    # Try Edge InPrivate, then fall back to default browser.
    # On WSL, Edge is a Windows executable under /mnt/c/.
    _WSL_EDGE = "/mnt/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
    edge = (
        shutil.which("microsoft-edge-stable")
        or shutil.which("microsoft-edge")
        or (_WSL_EDGE if Path(_WSL_EDGE).exists() else None)
    )
    if edge:
        print(
            f"{Colors.GREEN}Opening Grafana (Edge InPrivate) at: "
            f"{display_url}{Colors.RESET}"
        )
        subprocess.Popen(
            [edge, "--inprivate", display_url],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    else:
        print(f"{Colors.GREEN}Opening Grafana at: {display_url}{Colors.RESET}")
        webbrowser.open(display_url)


def main():
    parser = argparse.ArgumentParser(
        description="Run KServe inferencing tests on deployed cleanroom"
    )
    parser.add_argument(
        "--deployment-config-dir",
        required=True,
        help="Directory containing deployment-config.json",
    )
    parser.add_argument(
        "--out-dir",
        help="Output directory containing deployment artifacts",
    )
    parser.add_argument(
        "--contract-id",
        help="Contract ID to use (default: auto-generated)",
    )
    parser.add_argument(
        "--models",
        default="default",
        help="Comma-separated models to deploy: iris,tinyllama-gpu,gemma4-gpu,phi4-gpu,tinyllama,default "
        "(default: default, note: tinyllama-gpu, gemma4-gpu, and phi4-gpu require GPU and are not included in 'default')",
    )
    parser.add_argument(
        "--flex-node-vm-size",
        default="",
        help="VM size for the flex node (e.g. Standard_NCC40ads_H100_v5)",
    )
    parser.add_argument(
        "--provision-flex-node-using-baked-image",
        action="store_true",
        help="Whether to provision the flex node using baked in image instead of SSH.",
    )
    parser.add_argument(
        "--no-delete",
        action="store_true",
        default=False,
        help="Skip deleting any existing InferenceService before deploying. "
        "If the model spec is unchanged the update is a no-op.",
    )
    parser.add_argument(
        "--location",
        default="centralindia",
        help="Azure region for resource deployment (default: centralindia)",
    )
    parser.add_argument(
        "--open-grafana",
        action="store_true",
        default=False,
        help="Open Grafana dashboard in a browser via kubectl port-forward.",
    )

    args = parser.parse_args()

    valid_models = {
        "iris",
        "tinyllama-gpu",
        "gemma4-gpu",
        "phi4-gpu",
        "tinyllama",
        "default",
    }
    enabled_models = set(m.strip() for m in args.models.split(","))
    invalid_models = enabled_models - valid_models
    if invalid_models:
        parser.error(
            f"Invalid model(s): {', '.join(sorted(invalid_models))}. "
            f"Valid options: {', '.join(sorted(valid_models))}"
        )

    script_dir = Path(__file__).parent
    out_dir = args.out_dir or str(script_dir / "generated")
    deployment_config_dir = args.deployment_config_dir

    # Load deployment configuration
    config = load_deployment_config(deployment_config_dir)
    ccf_endpoint = config["ccf_endpoint"]
    registry_arg = config["registry_arg"]
    repo = config["repo"]
    tag = config["tag"]
    allow_all = config.get("allow_all", False)
    infra_type = config["infra_type"]
    owner_client = config["project_name"]
    owner_name = config["initial_member_name"]

    # Generate contract ID
    contract_id = args.contract_id or "inferencing-" + str(uuid.uuid4())[:8]

    # Determine if security policy should be used
    with_security_policy = infra_type == "aks" and not allow_all

    # Run scenario
    print(f"{Colors.CYAN}Running scenario...{Colors.RESET}")
    scenario_script = str(script_dir / "run-scenario.ps1")
    scenario_args = [
        "-registry",
        registry_arg,
        "-repo",
        repo,
        "-tag",
        tag,
        "-contractId",
        contract_id,
        "-outDir",
        out_dir,
        "-deploymentConfigDir",
        deployment_config_dir,
        "-ccfEndpoint",
        ccf_endpoint,
        "-ownerClient",
        owner_client,
        "-ownerName",
        owner_name,
        "-models",
        args.models,
        "-location",
        args.location,
    ]

    if args.flex_node_vm_size:
        scenario_args += ["-flexNodeVmSize", args.flex_node_vm_size]

    if args.provision_flex_node_using_baked_image:
        scenario_args.append("-provisionFlexNodeUsingBakedImage")

    if args.no_delete:
        scenario_args.append("-noDelete")

    if with_security_policy:
        scenario_args.append("-withSecurityPolicy")

    cmd = ["pwsh", scenario_script] + scenario_args
    print(f"{Colors.CYAN}Starting: {' '.join(cmd)}{Colors.RESET}")
    result = subprocess.run(cmd)
    if result.returncode != 0:
        print(
            f"{Colors.RED}Scenario script failed with exit code {result.returncode}{Colors.RESET}"
        )
        sys.exit(1)

    # Validate expected ledger/audit events were emitted.
    print(
        f"\n{Colors.CYAN}Checking for expected audit events in the ledger...{Colors.RESET}"
    )
    expected_models = _get_expected_model_names(args.models)
    _validate_ledger_events(contract_id, owner_client, expected_models)

    # Get telemetry with real-time output
    telemetry_script = str(script_dir / "get-telemetry.ps1")
    tail_telemetry_output(telemetry_script, out_dir, deployment_config_dir)

    # Check that expected output files got created
    print(f"\n{Colors.CYAN}Checking for expected output files...{Colors.RESET}")
    expected_files = [
        f"{out_dir}/telemetry/logs_kserve-inferencing-agent.json",
        f"{out_dir}/telemetry/traces_kserve-inferencing-agent.json",
        f"{out_dir}/telemetry/metrics_kserve-inferencing-frontend.json",
        f"{out_dir}/telemetry/logs_kserve-inferencing-frontend.json",
        f"{out_dir}/telemetry/traces_kserve-inferencing-frontend.json",
    ]

    missing_files = check_missing_files(expected_files)
    if missing_files:
        print_missing_files_error(missing_files)
        sys.exit(1)

    print(f"\n{Colors.GREEN}✅ All telemetry validations passed.{Colors.RESET}")

    # Open Grafana dashboard if requested. Done last so the port-forward
    # stays alive while the user browses. The script blocks until the user
    # presses Enter.
    if args.open_grafana:
        kube_config = f"{deployment_config_dir}/cl-cluster/k8s-credentials.yaml"
        open_grafana(kube_config)
        try:
            input(
                f"\n{Colors.CYAN}Grafana is running. "
                f"Press Enter to stop port-forward and exit.{Colors.RESET}\n"
            )
        except (KeyboardInterrupt, EOFError):
            pass


if __name__ == "__main__":
    main()
