#!/usr/bin/env python3
"""
Big Data Analytics Longhaul Test Script

This script repeatedly executes an already-approved query from the BVT run
and checks for its completion over a configurable duration. Between cycles,
it pauses for a configurable interval. Optionally, it performs chaos actions
(CCF recovery or frontend-service container restart) between cycles to
validate resilience.
"""

import argparse
import json
import os
import random
import subprocess
import sys
import time
from datetime import datetime, timedelta
from pathlib import Path

_git_root = subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
).stdout.strip()
sys.path.insert(0, str(Path(_git_root) / "test" / "onebox"))
# Import utilities from submit-sql-job.py (hyphenated filename requires importlib).
import importlib.util

from cleanroom_test_utils import Colors, run_command

_script_dir = Path(__file__).parent
_spec = importlib.util.spec_from_file_location(
    "submit_sql_job", str(_script_dir / "submit-sql-job.py")
)
assert _spec is not None and _spec.loader is not None
_submit_sql_job = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_submit_sql_job)

execute_sql_test_parallel = _submit_sql_job.execute_sql_test_parallel
start_kubectl_proxy = _submit_sql_job.start_kubectl_proxy
KUBECTL_PROXY_PORT = _submit_sql_job.KUBECTL_PROXY_PORT
ANALYTICS_ENDPOINT_READY_TIMEOUT_SECONDS = (
    _submit_sql_job.ANALYTICS_ENDPOINT_READY_TIMEOUT_SECONDS
)

import requests

FRONTEND_READY_TIMEOUT_SECONDS = 120


def trigger_ccf_recovery(node_count: int = 1):
    """Trigger CCF confidential recovery using recover-ccf.ps1."""
    script = str(Path(_git_root) / "samples" / "ccf" / "azcli" / "recover-ccf.ps1")
    print(
        f"{Colors.YELLOW}[Chaos] Triggering CCF confidential "
        f"recovery...{Colors.RESET}"
    )
    run_command(
        [
            "pwsh",
            script,
            "-nodeCount",
            str(node_count),
            "-confidentialRecovery",
        ]
    )
    # Validate governance state survived recovery.
    validate_script = str(
        Path(_git_root) / "samples" / "ccf" / "azcli" / "validate-cgs-recovery.ps1"
    )
    print(
        f"{Colors.YELLOW}[Chaos] Validating CGS state after "
        f"recovery...{Colors.RESET}"
    )
    run_command(["pwsh", validate_script])
    print(
        f"{Colors.GREEN}[Chaos] CCF recovery completed and " f"validated.{Colors.RESET}"
    )


def trigger_frontend_restart():
    """Restart the frontend-service Docker container."""
    print(
        f"{Colors.YELLOW}[Chaos] Restarting frontend-service "
        f"container...{Colors.RESET}"
    )
    run_command(["docker", "restart", "frontend-service"])

    # Wait for frontend to become ready.
    frontend_port = "61001"
    if (
        os.environ.get("CODESPACES") == "true"
        or os.environ.get("GITHUB_ACTIONS") == "true"
    ):
        frontend_host = "172.17.0.1"
    else:
        frontend_host = "localhost"
    ready_url = f"http://{frontend_host}:{frontend_port}/ready"

    start_time = time.time()
    while time.time() - start_time < FRONTEND_READY_TIMEOUT_SECONDS:
        try:
            resp = requests.get(ready_url, timeout=5)
            if resp.status_code == 200:
                print(
                    f"{Colors.GREEN}[Chaos] Frontend service is "
                    f"ready.{Colors.RESET}"
                )
                return
        except Exception:
            pass
        time.sleep(3)
    raise TimeoutError("Frontend service did not become ready after restart.")


def perform_chaos_action(cycle_number: int):
    """Randomly perform a chaos action: CCF recovery or frontend restart."""
    action = random.choice(["ccf_recovery", "frontend_restart"])
    print(
        f"\n{Colors.YELLOW}[Chaos] Cycle {cycle_number} - selected "
        f"action: {action}{Colors.RESET}"
    )
    if action == "ccf_recovery":
        trigger_ccf_recovery()
    else:
        trigger_frontend_restart()


def run_longhaul_cycle(
    cycle_number: int,
    query_document_id: str,
    contract_id: str,
    consumer_cgs_client: str,
    kube_config: str,
    frontend_endpoint: str | None,
    collaboration_id: str,
):
    """Run a single longhaul cycle: execute an existing approved query."""
    print(
        f"\n{Colors.CYAN}{'=' * 80}{Colors.RESET}"
        f"\n{Colors.CYAN}Longhaul Cycle {cycle_number} - "
        f"Query: {query_document_id}{Colors.RESET}"
        f"\n{Colors.CYAN}{'=' * 80}{Colors.RESET}"
    )

    print(f"\n{Colors.CYAN}[Cycle {cycle_number}] Executing " f"query...{Colors.RESET}")
    result = execute_sql_test_parallel(
        test_name=f"Longhaul Cycle {cycle_number}",
        query_document_id=query_document_id,
        contract_id=contract_id,
        kube_config=kube_config,
        frontend_endpoint=frontend_endpoint,
        collaboration_id=collaboration_id,
        cgs_client=consumer_cgs_client,
    )

    if result["success"]:
        print(
            f"{Colors.GREEN}[Cycle {cycle_number}] Completed successfully "
            f"in {result['duration']:.2f}s{Colors.RESET}"
        )
    else:
        print(
            f"{Colors.RED}[Cycle {cycle_number}] Failed: "
            f"{result['error']}{Colors.RESET}"
        )

    return result


def main():
    parser = argparse.ArgumentParser(
        description="Run longhaul big data analytics tests"
    )
    parser.add_argument(
        "--deployment-config-dir",
        required=True,
        help="Directory containing deployment-config.json",
    )
    parser.add_argument(
        "--out-dir",
        default=None,
        help="Output directory containing submitSqlJobConfig.json",
    )
    parser.add_argument(
        "--duration-minutes",
        type=int,
        default=120,
        help="Total duration to run the longhaul test in minutes (default: 120)",
    )
    parser.add_argument(
        "--pause-minutes",
        type=int,
        default=30,
        help="Pause duration between cycles in minutes (default: 30)",
    )
    parser.add_argument(
        "--enable-chaos",
        action="store_true",
        help="Enable chaos actions (CCF recovery or frontend restart) between cycles",
    )
    args = parser.parse_args()

    script_dir = Path(__file__).parent
    out_dir = args.out_dir or str(script_dir / "generated")
    deployment_config_dir = args.deployment_config_dir

    # Load job configuration from previous BVT run.
    config_path = Path(out_dir) / "submitSqlJobConfig.json"
    if not config_path.exists():
        print(
            f"{Colors.RED}submitSqlJobConfig.json not found at {config_path}. "
            f"Run the big-data-analytics BVT first.{Colors.RESET}"
        )
        sys.exit(1)

    with open(config_path, "r") as f:
        job_config = json.load(f)

    contract_id = job_config["contractId"]
    consumer_cgs_client = job_config["consumerCgsClient"]
    frontend_endpoint = job_config["frontendEndpoint"]
    collaboration_id = job_config["collaborationId"]
    collaboration_config_file = job_config["collaborationConfigFile"]
    os.environ["CLEANROOM_COLLABORATION_CONFIG_FILE"] = collaboration_config_file

    # Reuse the CSV standard query that was already approved in the BVT run.
    query_document_id = job_config["queries"]["csv_standard"]

    kube_config = str(
        Path(deployment_config_dir) / "cl-cluster" / "k8s-credentials.yaml"
    )

    # Start kubectl proxy.
    _process, proxy_address = start_kubectl_proxy(kube_config)

    analytics_endpoint = (
        f"http://{proxy_address}:{KUBECTL_PROXY_PORT}/api/v1/namespaces/"
        f"cleanroom-spark-analytics-agent/services/"
        f"https:cleanroom-spark-analytics-agent:443/proxy"
    )

    # Wait for analytics endpoint to be ready.
    timeout = ANALYTICS_ENDPOINT_READY_TIMEOUT_SECONDS
    start_time = time.time()
    while time.time() - start_time < timeout:
        try:
            response = requests.get(
                f"{analytics_endpoint}/ready", verify=False, timeout=5
            )
            if response.status_code == 200:
                print(f"{Colors.GREEN}Analytics endpoint is ready{Colors.RESET}")
                break
        except Exception:
            pass
        print("Waiting for analytics endpoint to be ready...")
        time.sleep(3)
    else:
        raise TimeoutError("Hit timeout waiting for analytics endpoint to be ready.")

    if not frontend_endpoint:
        run_command(
            f"az cleanroom collaboration context set "
            f"--collaboration-name {consumer_cgs_client}"
        )

    total_duration = timedelta(minutes=args.duration_minutes)
    pause_duration = timedelta(minutes=args.pause_minutes)
    longhaul_start = datetime.now()
    deadline = longhaul_start + total_duration

    print(
        f"\n{Colors.CYAN}{'=' * 80}{Colors.RESET}"
        f"\n{Colors.CYAN}Starting longhaul test{Colors.RESET}"
        f"\n{Colors.CYAN}  Total duration: {args.duration_minutes} "
        f"minutes{Colors.RESET}"
        f"\n{Colors.CYAN}  Pause between cycles: {args.pause_minutes} "
        f"minutes{Colors.RESET}"
        f"\n{Colors.CYAN}  Deadline: {deadline.strftime('%Y-%m-%d %H:%M:%S')}"
        f"{Colors.RESET}"
        f"\n{Colors.CYAN}{'=' * 80}{Colors.RESET}"
    )

    cycle_number = 0
    results = []

    while datetime.now() < deadline:
        cycle_number += 1

        result = run_longhaul_cycle(
            cycle_number=cycle_number,
            query_document_id=query_document_id,
            contract_id=contract_id,
            consumer_cgs_client=consumer_cgs_client,
            kube_config=kube_config,
            frontend_endpoint=frontend_endpoint,
            collaboration_id=collaboration_id,
        )
        results.append(result)

        # Check if we still have time for another cycle.
        if datetime.now() >= deadline:
            print(
                f"\n{Colors.YELLOW}Deadline reached after cycle "
                f"{cycle_number}.{Colors.RESET}"
            )
            break

        # Optionally perform a chaos action between cycles.
        if args.enable_chaos:
            perform_chaos_action(cycle_number)

        # Pause between cycles.
        remaining = deadline - datetime.now()
        actual_pause = min(pause_duration, remaining)
        if actual_pause.total_seconds() <= 0:
            break

        pause_end = datetime.now() + actual_pause
        print(
            f"\n{Colors.YELLOW}Pausing for "
            f"{int(actual_pause.total_seconds() / 60)} minutes until "
            f"{pause_end.strftime('%H:%M:%S')}...{Colors.RESET}"
        )
        time.sleep(actual_pause.total_seconds())

    # Print summary.
    elapsed = datetime.now() - longhaul_start
    passed = sum(1 for r in results if r["success"])
    failed = sum(1 for r in results if not r["success"])

    print(
        f"\n{Colors.CYAN}{'=' * 80}{Colors.RESET}"
        f"\n{Colors.CYAN}Longhaul Test Summary{Colors.RESET}"
        f"\n{Colors.CYAN}{'=' * 80}{Colors.RESET}"
        f"\n  Total cycles:  {len(results)}"
        f"\n  Passed:        {passed}"
        f"\n  Failed:        {failed}"
        f"\n  Elapsed time:  {str(elapsed).split('.')[0]}"
    )

    for r in results:
        status = (
            f"{Colors.GREEN}PASSED{Colors.RESET}"
            if r["success"]
            else f"{Colors.RED}FAILED{Colors.RESET}"
        )
        print(f"  Cycle {r['name']}: {status} " f"({r['duration']:.2f}s)")

    if failed > 0:
        print(f"\n{Colors.RED}{failed} cycle(s) failed.{Colors.RESET}")
        sys.exit(1)

    print(
        f"\n{Colors.GREEN}All {passed} cycle(s) passed " f"successfully!{Colors.RESET}"
    )


if __name__ == "__main__":
    main()
