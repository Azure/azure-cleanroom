#!/usr/bin/env python3
"""
Shared utilities for cleanroom test scripts.

This module provides common functionality for orchestrating cleanroom
deployments and testing, including parallel execution, output streaming,
and color-coded terminal output.
"""

import hashlib
import json
import subprocess
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple, Union


class Colors:
    """ANSI color codes for terminal output"""

    GREEN = "\033[92m"
    RED = "\033[91m"
    YELLOW = "\033[93m"
    BLUE = "\033[94m"
    CYAN = "\033[96m"
    RESET = "\033[0m"


def read_data_file(file_path: Path, format: str) -> List[Dict]:
    """Read data file based on format (csv, json, or parquet).

    Args:
        file_path: Path to the data file
        format: Data format ('csv', 'json', or 'parquet')

    Returns:
        List of dictionaries representing the data rows

    Raises:
        ValueError: If format is not supported
    """
    if format == "csv":
        import csv

        with open(file_path, "r") as f:
            reader = csv.DictReader(f)
            return list(reader)
    elif format == "json":
        with open(file_path, "r") as f:
            lines = f.readlines()
            if len(lines) == 1:
                data = json.loads(lines[0])
                return data if isinstance(data, list) else [data]
            else:
                return [json.loads(line.strip()) for line in lines if line.strip()]
    elif format == "parquet":
        import pandas as pd

        df = pd.read_parquet(file_path)
        return df.to_dict(orient="records")
    else:
        raise ValueError(f"Unsupported format: {format}")


def get_unique_string(id_str: str, length: int = 13) -> str:
    """Generate a unique string from an ID using SHA512 hash.

    Args:
        id_str: Input string to hash
        length: Length of the output string (default: 13)

    Returns:
        A lowercase alphabetic string of specified length
    """
    hash_obj = hashlib.sha512(id_str.encode())
    hash_bytes = hash_obj.digest()
    result = ""
    for i in range(1, length + 1):
        result += chr(hash_bytes[i] % 26 + ord("a"))
    return result


def run_command(
    cmd: Union[str, List[str]], capture_output: bool = True
) -> subprocess.CompletedProcess:
    """Run a shell command and return the result.

    Args:
        cmd: Command as a string (will be split on spaces) or list of strings
        capture_output: Whether to capture stdout/stderr

    Returns:
        CompletedProcess with the command result
    """
    cmd_list = cmd.split() if isinstance(cmd, str) else cmd
    result = subprocess.run(
        cmd_list, check=False, capture_output=capture_output, text=True
    )
    try:
        result.check_returncode()
    except subprocess.CalledProcessError:
        for line in result.stdout.splitlines():
            print(line)
        for line in result.stderr.splitlines():
            print(line)
        raise
    return result


def tail_output(process: subprocess.Popen, prefix: str, color: str) -> None:
    """
    Tail output from a process in real-time with colored prefix.

    Args:
        process: Subprocess with stdout to tail
        prefix: Prefix label for output lines
        color: ANSI color code for the prefix
    """
    if process.stdout:
        for line in iter(process.stdout.readline, ""):
            if line:
                print(f"{color}[{prefix}]{Colors.RESET} {line.rstrip()}")


def tail_telemetry_output(
    script_path: str, out_dir: str, deployment_config_dir: str | None = None
) -> None:
    """
    Run get-telemetry.ps1 and tail its output in real-time.

    Args:
        script_path: Path to the get-telemetry.ps1 script
        out_dir: Output directory for telemetry data
        deployment_config_dir: Directory containing deployment configuration (optional)

    Raises:
        CalledProcessError: If telemetry collection fails
    """
    print(
        f"\n{Colors.CYAN}Starting telemetry collection with real-time output...{Colors.RESET}\n"
    )

    try:
        cmd = ["pwsh", script_path, "-outDir", out_dir]
        if deployment_config_dir:
            cmd.extend(["-deploymentConfigDir", deployment_config_dir])
        print(f"{Colors.YELLOW}[TELEMETRY] Command: {' '.join(cmd)}{Colors.RESET}")

        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
            universal_newlines=True,
        )

        # Tail output in real-time
        tail_output(process, "TELEMETRY", Colors.YELLOW)

        # Wait for completion
        returncode = process.wait()

        if returncode == 0:
            print(
                f"{Colors.GREEN}[TELEMETRY] ✓ Telemetry collection completed successfully{Colors.RESET}"
            )
        else:
            print(
                f"{Colors.RED}[TELEMETRY] ✗ Telemetry collection failed with exit code {returncode}{Colors.RESET}"
            )
            raise subprocess.CalledProcessError(returncode, cmd)

    except Exception as e:
        print(
            f"{Colors.RED}[TELEMETRY] ✗ Exception during telemetry collection: {e}{Colors.RESET}"
        )
        raise


def run_script_with_output(
    script_path: str, args: List[str], name: str, color: str
) -> Dict[str, Any]:
    """
    Run a PowerShell script with real-time output streaming.

    Args:
        script_path: Path to the PowerShell script
        args: Command-line arguments for the script
        name: Display name for the script execution
        color: ANSI color code for output

    Returns:
        Dict with execution results containing:
        - name: Display name
        - success: Whether execution succeeded
        - error: Error message if failed
        - returncode: Process exit code
    """
    result = {"name": name, "success": False, "error": None, "returncode": None}

    try:
        cmd = ["pwsh", script_path] + args
        print(f"{color}[{name}] Starting: {' '.join(cmd)}{Colors.RESET}")

        process = subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            bufsize=1,
            universal_newlines=True,
        )

        # Tail output in real-time
        tail_output(process, name, color)

        # Wait for completion
        returncode = process.wait()
        result["returncode"] = returncode

        if returncode == 0:
            result["success"] = True
            print(f"{Colors.GREEN}[{name}] ✓ Completed successfully{Colors.RESET}")
        else:
            result["error"] = f"Process exited with code {returncode}"
            print(
                f"{Colors.RED}[{name}] ✗ Failed with exit code {returncode}{Colors.RESET}"
            )

    except Exception as e:
        result["error"] = str(e)
        print(f"{Colors.RED}[{name}] ✗ Exception: {e}{Colors.RESET}")

    return result


def check_missing_files(expected_files: List[str]) -> List[str]:
    """
    Check which expected files are missing.

    Args:
        expected_files: List of file paths to check

    Returns:
        List of missing file paths
    """
    missing_files = []
    for file_path in expected_files:
        if not Path(file_path).exists():
            missing_files.append(file_path)
    return missing_files


def print_missing_files_error(missing_files: List[str]) -> None:
    """
    Print error message for missing files.

    Args:
        missing_files: List of missing file paths
    """
    print(
        f"{Colors.RED}Did not find the following expected file(s). Check clean room logs for any failure(s):{Colors.RESET}"
    )
    for file_path in missing_files:
        print(f"{Colors.RED}  - {file_path}{Colors.RESET}")


def deploy_virtual_cluster_governance(
    root: str,
    out_dir: str,
    no_build: bool,
    registry: str,
    repo: str,
    tag: str,
    ccf_project_name: str,
    project_name: str,
    initial_member_name: str,
    cluster_provider_project_name: str,
    cluster_name: str,
    enable_monitoring: bool = False,
    max_workers: int = 2,
) -> Tuple[bool, Optional[str]]:
    """
    Deploy virtual cleanroom governance and cluster in parallel.

    Args:
        root: Repository root path
        out_dir: Output directory for deployment artifacts
        no_build: Whether to skip building container images
        registry: Container registry to use
        repo: Container repository
        tag: Container image tag
        ccf_project_name: CCF project name
        project_name: Governance project name
        initial_member_name: Initial member name for governance
        cluster_provider_project_name: Cluster provider project name
        cluster_name: Cluster name
        max_workers: Maximum number of parallel workers (default: 2)

    Returns:
        Tuple of (success, ccf_endpoint) where:
        - success: Whether deployment succeeded
        - ccf_endpoint: CCF endpoint URL if successful, None otherwise
    """
    print(
        f"\n{Colors.CYAN}Starting parallel deployment of governance and cluster...{Colors.RESET}\n"
    )

    # Prepare arguments for governance deployment
    governance_args = [
        "-NoBuild" if no_build else "",
        "-registry",
        registry,
        "-repo",
        repo,
        "-tag",
        tag,
        "-ccfProjectName",
        ccf_project_name,
        "-projectName",
        project_name,
        "-initialMemberName",
        initial_member_name,
        "-outDir",
        out_dir,
    ]
    governance_args = [arg for arg in governance_args if arg]  # Remove empty strings

    # Prepare arguments for cluster deployment
    cluster_args = [
        "-NoBuild" if no_build else "",
        "-registry",
        registry,
        "-repo",
        repo,
        "-tag",
        tag,
        "-clusterProviderProjectName",
        cluster_provider_project_name,
        "-clusterName",
        cluster_name,
        "-outDir",
        out_dir,
    ]
    if enable_monitoring:
        cluster_args.append("-enableMonitoring")
    cluster_args = [arg for arg in cluster_args if arg]  # Remove empty strings

    # Prepare arguments for frontend deployment
    frontend_args = [
        "-NoBuild" if no_build else "",
        "-repo",
        repo,
        "-tag",
        tag,
    ]
    frontend_args = [arg for arg in frontend_args if arg]  # Remove empty strings

    governance_script = (
        f"{root}/test/onebox/multi-party-collab/deploy-virtual-cleanroom-governance.ps1"
    )
    cluster_script = (
        f"{root}/test/onebox/multi-party-collab/deploy-virtual-cleanroom-cluster.ps1"
    )
    frontend_script = f"{root}/test/onebox/frontendservice/deploy-frontend-service.ps1"

    # Execute in parallel
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = {
            executor.submit(
                run_script_with_output,
                governance_script,
                governance_args,
                "CCF-Governance",
                Colors.BLUE,
            ): "CCF-Governance",
            executor.submit(
                run_script_with_output,
                cluster_script,
                cluster_args,
                "Kind-Cluster",
                Colors.CYAN,
            ): "Kind-Cluster",
            executor.submit(
                run_script_with_output,
                frontend_script,
                frontend_args,
                "Frontend-Service",
                Colors.YELLOW,
            ): "Frontend-Service",
        }

        results = {}
        for future in as_completed(futures):
            deployment_type = futures[future]
            results[deployment_type] = future.result()
            remaining = [futures[f] for f in futures if not f.done()]
            if remaining:
                print(
                    f"{Colors.CYAN}  ⏳ Still running: "
                    f"{', '.join(remaining)}{Colors.RESET}"
                )

    # Check results
    all_success = all(r["success"] for r in results.values())

    if not all_success:
        print(f"\n{Colors.RED}Parallel deployment failed:{Colors.RESET}")
        for name, result in results.items():
            if not result["success"]:
                print(
                    f"{Colors.RED}  - {name}: {result.get('error', 'Unknown error')}{Colors.RESET}"
                )
        return False, None

    print(
        f"\n{Colors.GREEN}✓ Parallel deployment completed successfully{Colors.RESET}\n"
    )

    # Get CCF endpoint
    ccf_json_path = Path(out_dir) / "ccf" / "ccf.json"
    with open(ccf_json_path, "r") as f:
        ccf_data = json.load(f)
        ccf_endpoint = ccf_data["endpoint"]

    return True, ccf_endpoint


def deploy_aks_cluster_governance(
    root: str,
    out_dir: str,
    no_build: bool,
    registry: str,
    repo: str,
    tag: str,
    allow_all: bool,
    isv_resource_group: str,
    cl_cluster_resource_group: str,
    ccf_name: str,
    cluster_name: str,
    location: str,
    project_name: str,
    initial_member_name: str,
    ccf_provider_project_name: str,
    cluster_provider_project_name: str,
    max_workers: int = 2,
) -> Tuple[bool, Optional[str]]:
    """
    Deploy CACI (Container-based ACI) governance and AKS cluster in parallel.

    Args:
        root: Repository root path
        out_dir: Output directory for deployment artifacts
        no_build: Whether to skip building container images
        registry: Container registry to use
        repo: Container repository
        tag: Container image tag
        allow_all: Whether to allow all security policies
        isv_resource_group: ISV resource group name
        cl_cluster_resource_group: Cluster resource group name
        ccf_name: CCF network name
        cluster_name: Cluster name
        location: Azure location
        project_name: Governance project name
        initial_member_name: Initial member name for governance
        ccf_provider_project_name: CCF provider project name
        cluster_provider_project_name: Cluster provider project name
        max_workers: Maximum number of parallel workers (default: 2)

    Returns:
        Tuple of (success, ccf_endpoint) where:
        - success: Whether deployment succeeded
        - ccf_endpoint: CCF endpoint URL if successful, None otherwise
    """
    print(
        f"\n{Colors.CYAN}Starting parallel deployment of ACI governance and cluster...{Colors.RESET}\n"
    )

    # Prepare arguments for governance deployment
    governance_args = [
        "-resourceGroup",
        isv_resource_group,
        "-ccfName",
        ccf_name,
        "-location",
        location,
        "-registry",
        registry,
        "-repo",
        repo,
        "-tag",
        tag,
        "-projectName",
        project_name,
        "-initialMemberName",
        initial_member_name,
        "-outDir",
        out_dir,
        "-ccfProviderProjectName",
        ccf_provider_project_name,
    ]
    if no_build:
        governance_args.insert(0, "-NoBuild")
    if allow_all:
        governance_args.append("-allowAll")

    # Prepare arguments for cluster deployment
    cluster_args = [
        "-resourceGroup",
        cl_cluster_resource_group,
        "-clusterName",
        cluster_name,
        "-location",
        location,
        "-registry",
        registry,
        "-repo",
        repo,
        "-tag",
        tag,
        "-outDir",
        out_dir,
        "-clusterProviderProjectName",
        cluster_provider_project_name,
    ]
    if no_build:
        cluster_args.insert(0, "-NoBuild")

    # Prepare arguments for frontend deployment
    frontend_args = [
        "-NoBuild" if no_build else "",
        "-repo",
        repo,
        "-tag",
        tag,
        "-outDir",
        out_dir,
        "-deployOnAKS",
        "-allowAll" if allow_all else "",
    ]
    frontend_args = [arg for arg in frontend_args if arg]  # Remove empty strings

    governance_script = (
        f"{root}/test/onebox/multi-party-collab/deploy-caci-cleanroom-governance.ps1"
    )
    cluster_script = (
        f"{root}/test/onebox/multi-party-collab/deploy-aks-cleanroom-cluster.ps1"
    )
    frontend_script = f"{root}/test/onebox/frontendservice/deploy-frontend-service.ps1"

    # Execute in parallel
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = {
            executor.submit(
                run_script_with_output,
                governance_script,
                governance_args,
                "CCF-Governance",
                Colors.BLUE,
            ): "CCF-Governance",
            executor.submit(
                run_script_with_output,
                cluster_script,
                cluster_args,
                "Aks-Cluster",
                Colors.CYAN,
            ): "Aks-Cluster",
        }

        results = {}
        for future in as_completed(futures):
            deployment_type = futures[future]
            results[deployment_type] = future.result()
            remaining = [futures[f] for f in futures if not f.done()]
            if remaining:
                print(
                    f"{Colors.CYAN}  ⏳ Still running: "
                    f"{', '.join(remaining)}{Colors.RESET}"
                )

    # Check results
    all_success = all(r["success"] for r in results.values())

    if not all_success:
        print(f"\n{Colors.RED}Parallel deployment failed:{Colors.RESET}")
        for name, result in results.items():
            if not result["success"]:
                print(
                    f"{Colors.RED}  - {name}: {result.get('error', 'Unknown error')}{Colors.RESET}"
                )
        return False, None

    print(
        f"\n{Colors.GREEN}✓ Parallel deployment completed successfully{Colors.RESET}\n"
    )

    print(f"{Colors.CYAN}Starting sequential deployment...{Colors.RESET}\n")
    # Deploy frontend service
    frontend_result = run_script_with_output(
        frontend_script, frontend_args, "Frontend-Service", Colors.YELLOW
    )
    if not frontend_result["success"]:
        print(
            f"{Colors.RED}Frontend service deployment failed: {frontend_result.get('error', 'Unknown error')}{Colors.RESET}"
        )
        return False, None

    # Get CCF endpoint
    print(f"{Colors.CYAN}Getting CCF endpoint...{Colors.RESET}")
    result = run_command(
        f"az cleanroom ccf network show "
        f"--name {ccf_name} "
        f"--provider-config {out_dir}/ccf/providerConfig.json "
        f"--provider-client {ccf_provider_project_name}"
    )
    response = json.loads(result.stdout)
    ccf_endpoint = response["endpoint"]

    return True, ccf_endpoint
