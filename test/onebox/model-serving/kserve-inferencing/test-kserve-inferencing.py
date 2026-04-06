#!/usr/bin/env python3
"""
KServe Inferencing Test Script

This script runs the KServe inferencing scenario tests on a pre-deployed
cleanroom environment. It expects a deployment-config.json file to be present
in the output directory.
"""

import argparse
import json
import subprocess
import sys
import uuid
from pathlib import Path

_git_root = subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
).stdout.strip()
sys.path.insert(0, str(Path(_git_root) / "test" / "onebox"))
from cleanroom_test_utils import (
    Colors,
    check_missing_files,
    print_missing_files_error,
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
        default="all",
        help="Comma-separated models to deploy: iris,gpt2,all (default: all)",
    )
    parser.add_argument(
        "--flex-node-vm-size",
        default="",
        help="VM size for the flex node (e.g. Standard_NCC40ads_H100_v5)",
    )

    args = parser.parse_args()

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
        "-models",
        args.models,
    ]

    if args.flex_node_vm_size:
        scenario_args += ["-flexNodeVmSize", args.flex_node_vm_size]

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

    # TODO (gsinha): Add any expected ledger events check.

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

    print(f"\n{Colors.GREEN}✅ All validations passed successfully!{Colors.RESET}")


if __name__ == "__main__":
    main()
