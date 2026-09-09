#!/usr/bin/env python3
"""
SQL Job Submission Test Script

This script executes SQL jobs in parallel for cleanroom analytics testing.
Converted from PowerShell to enable true parallel execution using Python's
multiprocessing capabilities.
"""

import argparse
import atexit
import json
import os
import re
import signal
import socket
import subprocess
import sys
import time
import uuid
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, Optional

import requests

# Import shared utilities
_git_root = subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
).stdout.strip()
sys.path.insert(0, str(Path(_git_root) / "test" / "onebox"))
from cleanroom_test_utils import Colors, run_command

# Constants.
FRONTEND_API_VERSION = "2026-03-01-preview"
KUBECTL_PROXY_PORT = 8181
KUBECTL_PROXY_START_TIMEOUT_SECONDS = 30
JOB_TIMEOUT_MINUTES = 30
STATUS_CHECK_INTERVAL_SECONDS = 15
ANALYTICS_ENDPOINT_READY_TIMEOUT_SECONDS = 60
MAX_RETRIES = 3
RETRY_DELAY_SECONDS = 5
RUN_HISTORY_MIN_EXPECTED_RUNS = 2
# Max concurrent Spark SQL apps on the AKS (--parallel) path. Each query submits a
# full Spark app whose executors run as confidential flex-node pods on a
# fixed-capacity cluster (scaleSku=small caps executors at 5). Running more than
# one at a time makes the apps contend for too few executor slots and time out
# (verified: parallel=4 starved one query to the 30-min timeout, build 176373058).
# Serialising conserves total executor-seconds, so it adds no real wall-clock while
# guaranteeing each app gets the whole cluster and completes.
MAX_PARALLEL_SQL_JOBS = 1
SPARK_APPLICATION_NAMESPACE = "analytics"
SPARK_APPLICATION_API_RESOURCE = "sparkapplications.sparkoperator.k8s.io"
SPARK_APPLICATION_LOOKUP_TIMEOUT_SECONDS = 60
SPARK_APPLICATION_LOOKUP_INTERVAL_SECONDS = 5

# Global variable to track kubectl proxy process
_kubectl_proxy_process: Optional[subprocess.Popen] = None


def get_access_token(cgs_client: str) -> str:
    """Get access token for the given CGS client."""
    result = run_command(
        f"az cleanroom governance client get-access-token "
        f"--query accessToken -o tsv --name {cgs_client}"
    )
    return result.stdout.strip()


def frontend_url(base: str) -> str:
    """Append api-version query parameter to a frontend URL."""
    sep = "&" if "?" in base else "?"
    return f"{base}{sep}api-version={FRONTEND_API_VERSION}"


def build_api_headers(token: str, use_frontend: bool = True) -> Dict[str, str]:
    """Build API headers with proper authorization."""
    auth_header = "Authorization" if use_frontend else "x-ms-cleanroom-authorization"
    return {
        "content-type": "application/json",
        auth_header: f"Bearer {token}",
    }


def cleanup_kubectl_proxy() -> None:
    """Cleanup function to terminate kubectl proxy on exit."""
    global _kubectl_proxy_process
    if _kubectl_proxy_process:
        print(f"\n{Colors.YELLOW}Cleaning up kubectl proxy...{Colors.RESET}")
        try:
            _kubectl_proxy_process.terminate()
            _kubectl_proxy_process.wait(timeout=5)
            print(f"{Colors.GREEN}kubectl proxy terminated successfully{Colors.RESET}")
        except subprocess.TimeoutExpired:
            print(
                f"{Colors.YELLOW}kubectl proxy did not terminate, killing...{Colors.RESET}"
            )
            _kubectl_proxy_process.kill()
            _kubectl_proxy_process.wait()
        except Exception as e:
            print(f"{Colors.RED}Error terminating kubectl proxy: {e}{Colors.RESET}")
        finally:
            _kubectl_proxy_process = None
            print_kubectl_proxy_log()


def print_kubectl_proxy_log() -> None:
    """Print the kubectl proxy log file contents."""
    log_file_path = Path("kubectl-proxy.log")
    if log_file_path.exists():
        print(f"\n{Colors.CYAN}{'=' * 80}{Colors.RESET}")
        print(f"{Colors.CYAN}kubectl proxy log contents:{Colors.RESET}")
        print(f"{Colors.CYAN}{'=' * 80}{Colors.RESET}")
        try:
            with open(log_file_path, "r") as f:
                print(f.read())
        except Exception as e:
            print(f"{Colors.RED}Error reading log file: {e}{Colors.RESET}")
        print(f"{Colors.CYAN}{'=' * 80}{Colors.RESET}\n")
    else:
        print(f"{Colors.YELLOW}kubectl proxy log file not found{Colors.RESET}")


def start_kubectl_proxy(
    kube_config: str,
    port: int = KUBECTL_PROXY_PORT,
) -> tuple[subprocess.Popen, str]:
    """
    Start kubectl proxy on the configured port and verify it started successfully.

    Args:
        kube_config: Path to kubeconfig file
        port: Port to use for kubectl proxy (default: KUBECTL_PROXY_PORT)
    Returns:
        The proxy process
        The address the proxy is listening on (e.g. localhost or 172.17.0.1)

    Raises:
        RuntimeError: If proxy fails to start
    """
    global _kubectl_proxy_process

    # Register cleanup handler
    atexit.register(cleanup_kubectl_proxy)

    # Handle signals for proper cleanup
    def signal_handler(signum, frame):
        cleanup_kubectl_proxy()
        sys.exit(0)

    signal.signal(signal.SIGINT, signal_handler)
    signal.signal(signal.SIGTERM, signal_handler)

    print(f"{Colors.CYAN}Starting kubectl proxy on port {port}...{Colors.RESET}")

    log_file_path = Path("kubectl-proxy.log")
    log_file = open(log_file_path, "w")

    proxy_address = "localhost"
    if (
        os.environ.get("GITHUB_ACTIONS") == "true"
        or os.environ.get("CODESPACES") == "true"
    ):
        proxy_address = "172.17.0.1"

    # Start kubectl proxy
    # Accept connections from localhost and Docker internal addresses.
    # This is required because:
    # 1. localhost/127.0.0.1: Standard local connections
    # 2. host.docker.internal: Docker Desktop (Mac/Windows) uses this to access host
    # 3. 172.17.0.1: Default Docker bridge network gateway IP on Linux
    # Without this, kubectl proxy rejects connections from Docker containers
    args = [
        "kubectl",
        "proxy",
        "--port",
        str(port),
        "--kubeconfig",
        kube_config,
        "--address",
        proxy_address,
        "--accept-hosts",
        "^(localhost|127\\.0\\.0\\.1|host\\.docker\\.internal|172\\.17\\.0\\.1)(:\\d+)?$",
        "-v",
        "6",
    ]
    process = subprocess.Popen(
        args,
        stdout=log_file,
        stderr=subprocess.STDOUT,
        text=True,
    )
    _kubectl_proxy_process = process

    # Wait for proxy to be ready (check if port is listening)
    max_wait = KUBECTL_PROXY_START_TIMEOUT_SECONDS
    start_time = time.time()

    while time.time() - start_time < max_wait:
        # Check if process is still running
        if process.poll() is not None:
            stdout, stderr = process.communicate()
            raise RuntimeError(
                f"kubectl proxy exited unexpectedly.\nStdout: {stdout}\nStderr: {stderr}"
            )

        # Try to connect to the proxy
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.settimeout(1)
                result = s.connect_ex((proxy_address, port))
                if result == 0:
                    print(
                        f"{Colors.GREEN}kubectl proxy started successfully on port {port}{Colors.RESET}"
                    )
                    return process, proxy_address
        except Exception:
            pass

        time.sleep(0.5)

    # Cleanup if we failed to start
    process.terminate()
    process.wait()
    raise RuntimeError(f"kubectl proxy failed to start within {max_wait} seconds")


def get_timestamp() -> str:
    """Get formatted timestamp for logging"""
    return datetime.now().strftime("[%m/%d/%y %H:%M:%S]")


def format_operational_events(events: list) -> str:
    """Format events array for readable output"""
    if not events:
        return "No events"

    # Sort by timestamp
    sorted_events = sorted(
        events, key=lambda e: e.get("lastTimestamp") or e.get("firstTimestamp") or ""
    )

    formatted = ["\n" + "=" * 80]
    for i, event in enumerate(sorted_events, 1):
        timestamp = event.get("lastTimestamp") or event.get("firstTimestamp") or "N/A"
        event_type = event.get("type", "N/A")
        reason = event.get("reason", "N/A")
        message = event.get("message", "N/A")
        count = event.get("count", 1)

        formatted.append(f"Event #{i}:")
        formatted.append(f"  Timestamp: {timestamp}")
        formatted.append(f"  Type:      {event_type}")
        formatted.append(f"  Reason:    {reason}")
        formatted.append(f"  Message:   {message}")
        formatted.append(f"  Count:     {count}")
        formatted.append("-" * 80)

    return "\n".join(formatted)


def format_audit_events(events: list) -> str:
    """Format audit events array for readable output"""
    if not events:
        return "No audit events"

    # Sort by timestamp
    sorted_events = sorted(events, key=lambda e: e.get("timestamp") or "")

    formatted = ["\n" + "=" * 80]
    for i, event in enumerate(sorted_events, 1):
        timestamp_iso = event.get("timestampIso") or "N/A"
        message = event.get("data", {}).get("message", "N/A")
        source = event.get("data", {}).get("source", "N/A")

        formatted.append(f"Audit Event #{i}:")
        formatted.append(f"  Timestamp: {timestamp_iso}")
        formatted.append(f"  Source:    {source}")
        formatted.append(f"  Message:   {message}")
        formatted.append("-" * 80)

    return "\n".join(formatted)


def get_audit_events(
    contract_id: str,
    job_id: str,
    frontend_endpoint: Optional[str] = None,
    collaboration_id: Optional[str] = None,
    cgs_client: Optional[str] = None,
) -> list:
    if frontend_endpoint:
        if not collaboration_id or not cgs_client:
            raise ValueError(
                "collaboration_id and cgs_client required when using frontend_endpoint"
            )

        # Get access token
        token = get_access_token(cgs_client)

        # Call frontend API
        url = frontend_url(
            f"{frontend_endpoint}/collaborations/{collaboration_id}/analytics/auditevents"
        )
        headers = build_api_headers(token, use_frontend=True)
        response = requests.get(url, headers=headers, verify=False)
        response.raise_for_status()

        events = response.json()
    else:
        # Use Azure CLI
        result = run_command(
            f"az cleanroom governance contract event list "
            f"--contract-id {contract_id} "
            f"--all "
            f"--governance-client ob-cr-publisher-user-client"
        )
        events = json.loads(result.stdout)

    job_id_stripped = job_id.replace("cl-spark-", "")
    candidate_audit_events = [
        x
        for x in events.get("value", [])
        if any(
            [
                re.search(
                    f"job id: {job_id_stripped}",
                    x.get("data", {}).get("message", ""),
                )
            ]
        )
    ]

    return candidate_audit_events


def validate_audit_events(
    audit_events: list, expected_audit_events: Dict[str, int] = None
) -> None:
    print(f"{get_timestamp()} Validating expected audit events...")

    event_counts = {}
    for event in audit_events:
        message = event.get("data", {}).get("message", "Unknown")
        # The message is of the form "message": "Event_DATASET_LOAD_COMPLETED_3001 | Dataset: consumer-input-7aaf05c3 load completed successfully | job id: 7429565a".
        # Treat Event_DATASET_LOAD_COMPLETED_3001 as the event id.
        event_id = message.split("|")[0].strip()
        event_counts[event_id] = event_counts.get(event_id, 0) + 1

    mismatches = []
    for expected_reason, expected_count in expected_audit_events.items():
        match = [x for x in event_counts.keys() if re.search(expected_reason, x)]
        if not len(match) > 0:
            mismatches.append(f"Event '{expected_reason}': expected but not found")
            continue
        if event_counts[match[0]] != expected_count:
            mismatches.append(
                f"Event '{expected_reason}': expected {expected_count}, got {event_counts[match[0]]}"
            )

    unmatched_reasons = []
    for actual_reason in event_counts.keys():
        is_expected = [
            re.search(expected_reason, actual_reason)
            for expected_reason in expected_audit_events.keys()
        ]
        if not any(is_expected):
            unmatched_reasons.append(
                f"{actual_reason} (count: {event_counts[actual_reason]})"
            )

    if unmatched_reasons:
        warning_msg = (
            f"{Colors.YELLOW}Warning: The following event reasons are not being matched:\n  "
            + "\n  ".join(unmatched_reasons)
            + f"{Colors.RESET}"
        )
        print(warning_msg)

    if mismatches:
        error_msg = "Event validation failed:\n" + "\n".join(mismatches)
        print(f"{Colors.RED}{error_msg}{Colors.RESET}")
        raise RuntimeError(error_msg)

    print(
        f"{Colors.GREEN}All expected audit events validated successfully.{Colors.RESET}"
    )


def validate_operational_events(
    events: list, expected_operational_events: Dict[str, int]
) -> None:
    print(f"{get_timestamp()} Validating expected events...")
    event_counts = {}
    for event in events:
        reason = event.get("reason", "Unknown")
        count = event.get("count", 1)
        event_counts[reason] = event_counts.get(reason, 0) + count

    mismatches = []
    for expected_reason, expected_count in expected_operational_events.items():
        actual_count = event_counts.get(expected_reason, 0)
        if actual_count != expected_count:
            mismatches.append(
                f"Event '{expected_reason}': expected {expected_count}, got {actual_count}"
            )

    unmatched_reasons = []
    for actual_reason in event_counts.keys():
        if actual_reason not in expected_operational_events:
            # Exclude Spark-related event reasons
            if not actual_reason.startswith(
                ("SparkApplication", "SparkDriver", "SparkExecutor")
            ):
                unmatched_reasons.append(
                    f"{actual_reason} (count: {event_counts[actual_reason]})"
                )

    if unmatched_reasons:
        warning_msg = (
            f"{Colors.YELLOW}Warning: The following event reasons are not being matched:\n  "
            + "\n  ".join(unmatched_reasons)
            + f"{Colors.RESET}"
        )
        print(warning_msg)

    if mismatches:
        error_msg = "Event validation failed:\n" + "\n".join(mismatches)
        print(f"{Colors.RED}{error_msg}{Colors.RESET}")
        raise RuntimeError(error_msg)

    print(
        f"{Colors.GREEN}All expected operational events validated successfully.{Colors.RESET}"
    )


def run_pwsh_script(script_path: str, **kwargs) -> None:
    """Run a PowerShell script with arguments"""
    cmd = ["pwsh", script_path]
    for key, value in kwargs.items():
        cmd.extend([f"-{key}", str(value)])
    run_command(cmd)


def download_s3_bucket(bucket_name: str, dst_dir: str, script_dir: Path) -> None:
    """Download S3 bucket contents to local directory."""
    print(f"Downloading S3 bucket '{bucket_name}' to '{dst_dir}'..")
    run_pwsh_script(
        str(script_dir / "download-bucket.ps1"),
        bucketName=bucket_name,
        dst=dst_dir,
    )


def validate_s3_query_output(
    format_name: str,
    job_id: str,
    dst_dir: str,
    bucket_name: str,
    script_dir: Path,
    expected_row_count: int,
) -> Dict[str, Any]:
    """
    Validate the output of an S3 query by downloading and counting rows.

    Args:
        format_name: The data format (csv, json, parquet)
        job_id: The unique job identifier to isolate outputs
        out_dir: Output directory for downloads
        bucket_name: S3 bucket name to download from
        script_dir: Directory containing helper scripts
        expected_row_count: Expected number of rows in output
    """
    os.makedirs(dst_dir, exist_ok=True)
    download_s3_bucket(
        bucket_name=bucket_name, dst_dir=os.path.abspath(dst_dir), script_dir=script_dir
    )

    search_job_id = (
        job_id.replace("cl-spark-", "") if job_id.startswith("cl-spark-") else job_id
    )

    job_output_dirs = [
        d for d in Path(dst_dir).rglob("*") if d.is_dir() and d.name == search_job_id
    ]

    if not job_output_dirs:
        return {
            "success": False,
            "error": f"No output folder found for job_id '{search_job_id}' in bucket '{bucket_name}'",
            "row_count": 0,
        }
    job_output_dir = job_output_dirs[0]

    if format_name == "csv":
        output_files = list(job_output_dir.rglob("*.csv"))
    elif format_name == "json":
        output_files = list(job_output_dir.rglob("*.json"))
    else:
        output_files = list(job_output_dir.rglob("*.parquet"))

    if not output_files:
        return {
            "success": False,
            "error": f"No output files found in {dst_dir}",
            "row_count": 0,
        }
    from cleanroom_test_utils import read_data_file

    data = read_data_file(output_files[0], format_name)
    row_count = len(data)

    success = row_count == expected_row_count

    return {
        "success": success,
        "row_count": row_count,
        "expected_row_count": expected_row_count,
        "error": (
            None
            if success
            else f"Expected {expected_row_count} rows but found {row_count}"
        ),
    }


def get_run_history(
    query_id: str,
    cgs_client: str,
    frontend_endpoint: Optional[str] = None,
    collaboration_id: Optional[str] = None,
    analytics_endpoint: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Get the run history for a specific query ID via analytics agent.

    Args:
        query_id: The query document identifier
        analytics_endpoint: The analytics endpoint URL
        cgs_client: The CGS client name
        frontend_endpoint: The frontend endpoint URL
        collaboration_id: The collaboration ID

    Returns:
        Dict containing runs, summary, and latestRun.
    """
    token = get_access_token(cgs_client)
    if frontend_endpoint and collaboration_id:
        url = frontend_url(
            f"{frontend_endpoint}/collaborations/{collaboration_id}/analytics/queries/{query_id}/runs"
        )
        headers = build_api_headers(token, use_frontend=True)
    elif analytics_endpoint:
        url = f"{analytics_endpoint}/queries/{query_id}/runs"
        headers = build_api_headers(token, use_frontend=False)

    response = requests.get(url, headers=headers, verify=False)
    response.raise_for_status()
    return response.json()


def validate_run_history_response(
    history: Dict[str, Any],
    expect_success: bool,
    query_name: str,
) -> list[str]:
    """
    Validate the run history response from the API.

    Args:
        history: The run history response from get_run_history()
        expect_success: Whether runs should be successful (True) or failed (False)
        query_name: Friendly query name used in logging.

    Returns:
        List of validation error messages (empty if validation passed)
    """
    runs = history.get("runs", [])
    summary = history.get("summary", {})
    latest_run = history.get("latestRun")

    total_runs = summary.get("totalRuns", 0)
    successful_runs = summary.get("successfulRuns", 0)
    failed_runs = summary.get("failedRuns", 0)

    errors = []
    expected_name = "successful" if expect_success else "failed"

    if total_runs < RUN_HISTORY_MIN_EXPECTED_RUNS:
        print(
            f"{Colors.RED}  ✗ {query_name} has insufficient runs: {total_runs}/{RUN_HISTORY_MIN_EXPECTED_RUNS}{Colors.RESET}"
        )
        errors.append(
            f"Expected at least {RUN_HISTORY_MIN_EXPECTED_RUNS} runs for {query_name}, got {total_runs}"
        )

    # Validate runs array size matches total runs.
    if len(runs) != total_runs:
        errors.append(f"Expected {total_runs} runs in array, got {len(runs)}")

    # Validate latestRun exists and has expected status.
    if not latest_run:
        errors.append("latestRun is missing")
    elif latest_run.get("isSuccessful") != expect_success:
        errors.append(f"latestRun should be {expected_name}")

    if expect_success:
        print(
            f"  Outcome check for {query_name}: expected all successful runs (TotalRuns={total_runs}, successfulRuns={successful_runs}, failed={failed_runs})"
        )
        if failed_runs != 0:
            errors.append(f"Expected 0 failed runs for {query_name}, got {failed_runs}")
    else:
        print(
            f"  Outcome check for {query_name}: expected all failed runs (TotalRuns={total_runs}, successfulRuns={successful_runs}, failed={failed_runs})"
        )
        if successful_runs != 0:
            errors.append(
                f"Expected 0 successful runs for {query_name}, got {successful_runs}"
            )

    return errors


def get_persisted_job_events(
    job_id: str,
    cgs_client: str,
    frontend_endpoint: Optional[str] = None,
    collaboration_id: Optional[str] = None,
    analytics_endpoint: Optional[str] = None,
) -> list:
    """Fetch the persisted operational events for a job via the status endpoint.

    These events are served from the JobEventRecord CRD, so they remain
    retrievable after the underlying Kubernetes core events are
    garbage-collected.
    """
    token = get_access_token(cgs_client)
    if frontend_endpoint and collaboration_id:
        url = frontend_url(
            f"{frontend_endpoint}/collaborations/{collaboration_id}/analytics/runs/{job_id}"
        )
        headers = build_api_headers(token, use_frontend=True)
    elif analytics_endpoint:
        url = f"{analytics_endpoint}/status/{job_id}"
        headers = build_api_headers(token, use_frontend=False)
    else:
        raise ValueError(
            "Either frontend_endpoint with collaboration_id, or analytics_endpoint "
            "must be provided to fetch persisted job events"
        )

    response = requests.get(url, headers=headers, verify=False)
    response.raise_for_status()
    return response.json().get("events", [])


# Representative operational/statistics event reasons that must be present (and
# thus persisted / still served) for a seeded run of each query.
_PERSISTED_SUCCESS_EVENT_REASONS = ["QUERY_EXECUTION_COMPLETED", "QUERY_STATISTICS"]
_PERSISTED_FAILURE_EVENT_REASONS = ["QUERY_EXECUTION_FAILED"]


def submit_sql_job(
    query_document_id: str,
    start_date: Optional[str] = None,
    end_date: Optional[str] = None,
    dry_run: bool = False,
    use_optimizer: bool = False,
    frontend_endpoint: Optional[str] = None,
    collaboration_id: Optional[str] = None,
    cgs_client: Optional[str] = None,
    scale_sku: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Execute a SQL job and wait for completion.

    Args:
        query_document_id: The query document identifier
        out_dir: Output directory for job configuration
        kube_config: Path to kubeconfig file
        start_date: Optional start date in ISO format
        end_date: Optional end date in ISO format
        expect_failure: Whether the job is expected to fail
        dry_run: Whether to perform a dry run (returns SKU settings without execution)
        use_optimizer: Whether to use AI optimizer for Spark configuration
        scale_sku: Optional Spark scale SKU (small, medium, or large)
    """
    run_id = str(uuid.uuid4())[:8]

    if frontend_endpoint:
        if not collaboration_id or not cgs_client:
            raise ValueError(
                "collaboration_id and cgs_client required when frontend_endpoint is provided"
            )

        # Get access token
        token = get_access_token(cgs_client)
        request_body = {"runId": run_id}
        if start_date:
            request_body["startDate"] = start_date
        if end_date:
            request_body["endDate"] = end_date
        if dry_run:
            request_body["dryRun"] = dry_run
        if use_optimizer:
            request_body["useOptimizer"] = use_optimizer
        if scale_sku:
            request_body["scaleSku"] = scale_sku

        url = frontend_url(
            f"{frontend_endpoint}/collaborations/{collaboration_id}/analytics/queries/{query_document_id}/run"
        )
        headers = build_api_headers(token, use_frontend=True)
        response = requests.post(url, json=request_body, headers=headers, verify=False)
        response.raise_for_status()

        submission_result = response.json()

    # Submit the SQL job
    else:
        query_params = {}
        if start_date:
            query_params["startDate"] = start_date
        if end_date:
            query_params["endDate"] = end_date
        if dry_run:
            query_params["dryRun"] = dry_run
        if use_optimizer:
            query_params["useOptimizer"] = use_optimizer
        if scale_sku:
            query_params["scaleSku"] = scale_sku

        query_params_json = json.dumps(query_params)
        result = run_command(
            [
                "az",
                "cleanroom",
                "collaboration",
                "spark-sql",
                "execute",
                "--application-name",
                query_document_id,
                "--application-parameters",
                query_params_json,
            ]
        )

        submission_result = json.loads(result.stdout)

    return submission_result


def get_scale_sku_settings(submission_result: Dict[str, Any]) -> Dict[str, Any]:
    """Extract effective scale settings from a product submission response."""
    try:
        sku_settings = submission_result["skuSettings"]
        return {
            "driver_memory": sku_settings["driver"]["memory"],
            "executor_memory": sku_settings["executor"]["memory"],
            "max_executors": sku_settings["executor"]["instances"]["max"],
        }
    except KeyError as e:
        raise ValueError(
            f"Submission response is missing expected SKU settings field: {e}"
        ) from e


def validate_scale_sku_applied(
    job_id: str,
    kube_config: str,
    scale_sku: str,
    expected: Dict[str, Any],
) -> None:
    """Validate that the submitted SparkApplication spec reflects scaleSku."""
    normalized_sku = scale_sku.lower()
    deadline = time.time() + SPARK_APPLICATION_LOOKUP_TIMEOUT_SECONDS
    last_error = None

    print(
        f"{get_timestamp()} Validating scaleSku '{normalized_sku}' "
        f"on SparkApplication '{job_id}'..."
    )

    while time.time() < deadline:
        try:
            result = subprocess.run(
                [
                    "kubectl",
                    "get",
                    SPARK_APPLICATION_API_RESOURCE,
                    job_id,
                    "-n",
                    SPARK_APPLICATION_NAMESPACE,
                    "--kubeconfig",
                    kube_config,
                    "-o",
                    "json",
                ],
                check=False,
                capture_output=True,
                text=True,
            )
            if result.returncode != 0:
                last_error = result.stderr.strip() or result.stdout.strip()
                time.sleep(SPARK_APPLICATION_LOOKUP_INTERVAL_SECONDS)
                continue

            spark_app = json.loads(result.stdout)
            spec = spark_app["spec"]
            actual = {
                "driver_memory": spec["driver"]["memory"],
                "executor_memory": spec["executor"]["memory"],
                "max_executors": spec["dynamicAllocation"]["maxExecutors"],
            }

            mismatches = [
                f"{key}: expected {expected[key]}, got {actual[key]}"
                for key in expected
                if actual[key] != expected[key]
            ]
            if mismatches:
                raise RuntimeError(
                    f"scaleSku '{normalized_sku}' was not applied to {job_id}: "
                    + "; ".join(mismatches)
                )

            print(
                f"{Colors.GREEN}scaleSku '{normalized_sku}' validated: "
                f"driver={actual['driver_memory']}, "
                f"executor={actual['executor_memory']}, "
                f"maxExecutors={actual['max_executors']}{Colors.RESET}"
            )
            return
        except (json.JSONDecodeError, KeyError) as e:
            last_error = e

        time.sleep(SPARK_APPLICATION_LOOKUP_INTERVAL_SECONDS)

    raise TimeoutError(
        f"Timed out waiting to validate scaleSku '{normalized_sku}' on "
        f"SparkApplication '{job_id}'. Last error: {last_error}"
    )


def wait_for_job_completion(
    query_document_id: str,
    job_id: str,
    contract_id: str,
    kube_config: str,
    expect_failure: bool = False,
    frontend_endpoint: Optional[str] = None,
    collaboration_id: Optional[str] = None,
    cgs_client: Optional[str] = None,
    expected_events: Optional[Dict[str, Dict[str, int]]] = None,
) -> None:
    # Wait for job completion
    timeout_minutes = JOB_TIMEOUT_MINUTES
    start_time = time.time()

    print("Waiting for job execution to complete...")

    while True:
        elapsed = time.time() - start_time
        if elapsed > timeout_minutes * 60:
            raise TimeoutError(
                f"Hit timeout waiting for application {job_id} to complete execution."
            )

        print(f"{get_timestamp()} Checking status of job: {job_id}")

        try:
            if frontend_endpoint:
                token = get_access_token(cgs_client)
                # Use frontend API to check status
                url = frontend_url(
                    f"{frontend_endpoint}/collaborations/{collaboration_id}/analytics/runs/{job_id}"
                )
                headers = build_api_headers(token, use_frontend=True)
                response = requests.get(url, headers=headers, verify=False)
                response.raise_for_status()
                job_status = response.json()
            else:
                # Use Azure CLI to check status
                result = run_command(
                    f"az cleanroom collaboration spark-sql get-execution-status "
                    f"--application-name {query_document_id} "
                    f"--job-id {job_id}"
                )
                job_status = json.loads(result.stdout)
            state = job_status["status"]["applicationState"]["state"]
            operational_events = job_status.get("events", [])

            if state == "COMPLETED":
                print(
                    f"{Colors.GREEN}{get_timestamp()} Application has completed execution.{Colors.RESET}"
                )

                print(
                    f"{Colors.GREEN}{get_timestamp()} Application Operational events:{Colors.RESET}"
                )
                print(
                    f"{Colors.GREEN}{format_operational_events(operational_events)}{Colors.RESET}"
                )
                if frontend_endpoint:
                    audit_events = get_audit_events(
                        contract_id,
                        job_id,
                        frontend_endpoint=frontend_endpoint,
                        collaboration_id=collaboration_id,
                        cgs_client=cgs_client,
                    )
                else:
                    audit_events = get_audit_events(contract_id, job_id)
                print(
                    f"{Colors.GREEN}{get_timestamp()} Application Audit events:{Colors.RESET}"
                )
                print(
                    f"{Colors.GREEN}{format_audit_events(audit_events)}{Colors.RESET}"
                )
                if expected_events:
                    if expected_events.get("operational"):
                        validate_operational_events(
                            operational_events, expected_events["operational"]
                        )
                    if expected_events.get("audit"):
                        validate_audit_events(
                            audit_events=audit_events,
                            expected_audit_events=expected_events["audit"],
                        )

                driver_pod = job_status["status"]["driverInfo"]["podName"]
                print(
                    f"Checking that Spark driver pod '{driver_pod}' exited gracefully..."
                )

                script_dir = Path(__file__).parent
                run_pwsh_script(
                    str(script_dir / "wait-for-spark-driver-pod-termination.ps1"),
                    podName=driver_pod,
                    namespace="analytics",
                    kubeConfig=kube_config,
                )

                # Check executor pods
                all_executors_terminated = True
                executor_state = job_status["status"].get("executorState", {})

                for pod_name, pod_state in executor_state.items():
                    print(f"Executor pod: {pod_name}, Reported state: {pod_state}")
                    if pod_state not in ["COMPLETED", "FAILED"]:
                        print(
                            f"{Colors.RED}Pod '{pod_name}' is not TERMINATED{Colors.RESET}"
                        )
                        all_executors_terminated = False

                    if pod_state == "FAILED":
                        print(
                            f"{Colors.RED}Executor pod '{pod_name}' has FAILED state....{Colors.RESET}"
                        )

                if all_executors_terminated:
                    print("All executor pods are TERMINATED.")
                    break

                if elapsed > timeout_minutes * 60:
                    print(
                        f"{Colors.RED}One or more executor pods failed or are in an incorrect state.{Colors.RESET}"
                    )
                    raise TimeoutError(
                        "Hit timeout waiting for one or more executor pods to report COMPLETED state."
                    )

            elif state == "FAILED":
                if not expect_failure:
                    print(
                        f"{Colors.RED}{get_timestamp()} Application has failed execution.{Colors.RESET}"
                    )
                    raise RuntimeError("Application has failed execution.")

                print(
                    f"{Colors.GREEN}{get_timestamp()} Application has failed execution as expected.{Colors.RESET}"
                )
                if frontend_endpoint:
                    audit_events = get_audit_events(
                        contract_id,
                        job_id,
                        frontend_endpoint=frontend_endpoint,
                        collaboration_id=collaboration_id,
                        cgs_client=cgs_client,
                    )
                else:
                    audit_events = get_audit_events(contract_id, job_id)
                print(
                    f"{Colors.GREEN}{get_timestamp()} Application Operational events:{Colors.RESET}"
                )
                print(
                    f"{Colors.GREEN}{format_operational_events(operational_events)}{Colors.RESET}"
                )
                print(
                    f"{Colors.GREEN}{get_timestamp()} Application Audit events:{Colors.RESET}"
                )
                print(
                    f"{Colors.GREEN}{format_audit_events(audit_events)}{Colors.RESET}"
                )
                if expected_events:
                    if expected_events.get("operational"):
                        validate_operational_events(
                            operational_events, expected_events["operational"]
                        )
                    if expected_events.get("audit"):
                        validate_audit_events(
                            audit_events=audit_events,
                            expected_audit_events=expected_events["audit"],
                        )
                break
            print(f"Application {job_id} state is: {state}")
        except RuntimeError as e:
            print(f"{Colors.RED}Received runtime error: {e}{Colors.RESET}")
            raise
        except Exception as e:
            # Retry on transient errors
            print(f"{Colors.RED}Error while fetching job status: {e}{Colors.RESET}")

        print(
            f"Waiting for {STATUS_CHECK_INTERVAL_SECONDS} seconds before checking status again..."
        )
        time.sleep(STATUS_CHECK_INTERVAL_SECONDS)


def test_query_invocation(
    query_document_id: str,
    cgs_client: str,
    contract_id: str,
    expected_error_code: Optional[str] = None,
    expected_error_message: Optional[str] = None,
    max_retries: int = MAX_RETRIES,
    retry_delay: int = RETRY_DELAY_SECONDS,
    frontend_endpoint: Optional[str] = None,
    collaboration_id: Optional[str] = None,
    analytics_endpoint: Optional[str] = None,
    expected_events: Optional[Dict[str, Dict[str, int]]] = None,
) -> None:
    """
    Test query invocation with expected success or failure.

    Args:
        query_document_id: The query document identifier
        contract_id: The contract ID
        cgs_client: The CGS client name
        expected_error_code: Expected error code (if should fail)
        expected_error_message: Expected error message (if should fail)
        max_retries: Maximum number of retries for 500 errors
        retry_delay: Delay in seconds between retries
        frontend_endpoint: The frontend API endpoint
        collaboration_id: Collaboration ID
        expected_events: Dictionary with 'operational' and 'audit' keys mapping to event dictionaries
    """
    # Get access token
    token = get_access_token(cgs_client)

    # Generate run ID
    run_id = str(uuid.uuid4())[:8]

    # Make the request
    if frontend_endpoint and collaboration_id:
        url = frontend_url(
            f"{frontend_endpoint}/collaborations/{collaboration_id}/analytics/queries/{query_document_id}/run"
        )
        headers = build_api_headers(token, use_frontend=True)
    elif analytics_endpoint:
        url = f"{analytics_endpoint}/queries/{query_document_id}/run"
        headers = build_api_headers(token, use_frontend=False)
    payload = {"runId": run_id}

    for attempt in range(max_retries):
        try:
            response = requests.post(url, json=payload, headers=headers, verify=False)
            response.raise_for_status()

            if expected_error_code:
                raise RuntimeError(
                    f"Expected query invocation to fail with error code '{expected_error_code}', but it succeeded."
                )
            else:
                print("Query invocation passed as expected.")
                return

        except requests.HTTPError:
            # Check if it's a 500 error and we should retry
            if response.status_code >= 500 and attempt < max_retries - 1:
                print(
                    f"{Colors.YELLOW}Received {response.status_code} error, retrying in {retry_delay}s (attempt {attempt + 1}/{max_retries})...{Colors.RESET}"
                )
                time.sleep(retry_delay)
                continue

            if not expected_error_code:
                raise RuntimeError(
                    f"Expected query invocation to succeed, but it failed"
                )

            error_data = response.json()
            print(json.dumps(error_data, indent=2))

            error_code = error_data.get("error", {}).get("code")
            if error_code != expected_error_code:
                raise RuntimeError(
                    f"Expected error code '{expected_error_code}' but got '{error_code}'."
                )

            if expected_error_message:
                error_message = error_data.get("error", {}).get("message")
                if error_message != expected_error_message:
                    raise RuntimeError(
                        f"Expected error message '{expected_error_message}' but got '{error_message}'."
                    )

            print(
                f"Query invocation failed as expected with error code '{expected_error_code}'."
            )
            audit_events = get_audit_events(
                contract_id,
                run_id,
                frontend_endpoint=frontend_endpoint,
                collaboration_id=collaboration_id,
                cgs_client=cgs_client,
            )
            print(f"{get_timestamp()} Application Audit events:")
            print(f"{format_audit_events(audit_events)}")
            if expected_events and expected_events.get("audit"):
                validate_audit_events(
                    audit_events=audit_events,
                    expected_audit_events=expected_events["audit"],
                )
            return


def execute_sql_test_parallel(
    test_name: str,
    query_document_id: str,
    contract_id: str,
    kube_config: str,
    start_date: Optional[str] = None,
    end_date: Optional[str] = None,
    expect_failure: bool = False,
    expected_events: Optional[Dict[str, Dict[str, int]]] = None,
    validate_output: Optional[Dict[str, Any]] = None,
    dry_run: bool = False,
    use_optimizer: bool = False,
    frontend_endpoint: Optional[str] = None,
    collaboration_id: Optional[str] = None,
    cgs_client: Optional[str] = None,
    scale_sku: Optional[str] = None,
) -> Dict[str, Any]:
    """
    Execute a single SQL test (used for parallel execution).

    Returns:
        Dict with test results
    """
    result = {
        "name": test_name,
        "query_document_id": query_document_id,
        "success": False,
        "error": None,
        "duration": 0.0,
        "validation": None,
    }

    start_time = time.time()
    try:
        print(f"[{test_name}] Starting execution...")
        submission_args = {
            "query_document_id": query_document_id,
            "start_date": start_date,
            "end_date": end_date,
            "use_optimizer": use_optimizer,
            "frontend_endpoint": frontend_endpoint,
            "collaboration_id": collaboration_id,
            "cgs_client": cgs_client,
            "scale_sku": scale_sku,
        }

        submission_result = submit_sql_job(**submission_args, dry_run=dry_run)
        job_id = submission_result["id"]

        result["job_id"] = job_id
        if not dry_run:
            expected_scale_settings = get_scale_sku_settings(submission_result)
            validate_scale_sku_applied(
                job_id=job_id,
                kube_config=kube_config,
                scale_sku=scale_sku or "small",
                expected=expected_scale_settings,
            )
            if frontend_endpoint:
                wait_for_job_completion(
                    job_id=job_id,
                    query_document_id=query_document_id,
                    contract_id=contract_id,
                    kube_config=kube_config,
                    expect_failure=expect_failure,
                    expected_events=expected_events,
                    frontend_endpoint=frontend_endpoint,
                    collaboration_id=collaboration_id,
                    cgs_client=cgs_client,
                )
            else:
                wait_for_job_completion(
                    job_id=job_id,
                    query_document_id=query_document_id,
                    contract_id=contract_id,
                    kube_config=kube_config,
                    expect_failure=expect_failure,
                    expected_events=expected_events,
                )

        result["success"] = True
        if validate_output and not expect_failure and job_id:
            script_dir = Path(__file__).parent
            validation = validate_s3_query_output(
                format_name=validate_output["output_format"],
                job_id=job_id,
                dst_dir=validate_output["dst_dir"],
                bucket_name=validate_output["bucket_name"],
                script_dir=script_dir,
                expected_row_count=validate_output["expected_row_count"],
            )
            result["validation"] = validation

            if not validation["success"]:
                result["success"] = False
                result["error"] = f"Validation failed: {validation['error']}"
                print(
                    f"{Colors.RED}[{test_name}] Validation failed: {validation['error']}{Colors.RESET}"
                )
            else:
                print(
                    f"{Colors.GREEN}[{test_name}] Validation passed: {validation['row_count']} rows{Colors.RESET}"
                )

        result["duration"] = time.time() - start_time
        print(f"[{test_name}] ✓ Completed successfully in {result['duration']:.2f}s")

    except Exception as e:
        result["error"] = str(e)
        result["duration"] = time.time() - start_time
        print(
            f"{Colors.RED}[{test_name}] ✗ Failed after {result['duration']:.2f}s: {e}{Colors.RESET}"
        )
    return result


def run_history_validation_test(
    successful_query_id: str,
    failing_query_id: str,
    cgs_client: str,
    frontend_endpoint: str,
    collaboration_id: str,
    analytics_endpoint: str,
) -> None:
    """
    Validate run history API response for 1 successful and 1 failed query.

    Args:
        successful_query_id: Query ID that should have successful runs.
        failing_query_id: Query ID that should have failed runs.
        cgs_client: The CGS client name.
        analytics_endpoint: The analytics endpoint URL.
    """
    print("\n=== Run History Validation Test ===")

    validation_targets = [
        (successful_query_id, True),
        (failing_query_id, False),
    ]

    all_errors = []
    for query_id, expect_success in validation_targets:
        print(
            f"\n{Colors.CYAN}Validating run history for {query_id} query...{Colors.RESET}"
        )
        history = get_run_history(
            query_id=query_id,
            cgs_client=cgs_client,
            frontend_endpoint=frontend_endpoint,
            collaboration_id=collaboration_id,
            analytics_endpoint=analytics_endpoint,
        )
        errors = validate_run_history_response(
            history=history,
            expect_success=expect_success,
            query_name=query_id,
        )
        all_errors.extend([f"[{query_id}] {error}" for error in errors])

    if all_errors:
        print(f"\n{Colors.RED}Run history validation failed:{Colors.RESET}")
        for error in all_errors:
            print(f"{Colors.RED}  - {error}{Colors.RESET}")
        sys.exit(1)

    print(f"\n{Colors.GREEN}✅ Job Run history validation passed!{Colors.RESET}")


def job_events_validation_test(
    successful_job_ids: list[str],
    failing_job_ids: list[str],
    cgs_client: str,
    frontend_endpoint: str,
    collaboration_id: str,
    analytics_endpoint: str,
) -> None:
    """Validate that operational events persist (via the JobEventRecord CRD)
    for prior runs, not just the most recent one.

    The JobEventRecord CRD exists so a job's operational/statistics events
    survive the ~1h Kubernetes core-event garbage collection. This test
    re-fetches the events for a seeded run of each query and asserts the status
    endpoint still serves them, proving they are read back from the CRD rather
    than from live (and now stale) Kubernetes events.

    Args:
        successful_job_ids: Job ids of seeded successful runs.
        failing_job_ids: Job ids of seeded failing runs.
        cgs_client: The CGS client name.
        frontend_endpoint: The frontend endpoint URL.
        collaboration_id: The collaboration ID.
        analytics_endpoint: The analytics endpoint URL.
    """
    print("\n=== Job Events Persistence Validation Test ===")

    validation_targets = [
        (successful_job_ids, _PERSISTED_SUCCESS_EVENT_REASONS, "successful"),
        (failing_job_ids, _PERSISTED_FAILURE_EVENT_REASONS, "failing"),
    ]

    all_errors = []
    for job_ids, expected_reasons, label in validation_targets:
        if not job_ids:
            all_errors.append(f"No seeded {label} runs available for events validation")
            continue

        # Any one seeded run is enough - runs are short-lived, so we only need to
        # confirm a run's events are persisted and served back.
        job_id = job_ids[0]
        print(
            f"\n{Colors.CYAN}Validating persisted events for {label} run "
            f"{job_id}...{Colors.RESET}"
        )
        events = get_persisted_job_events(
            job_id=job_id,
            cgs_client=cgs_client,
            frontend_endpoint=frontend_endpoint,
            collaboration_id=collaboration_id,
            analytics_endpoint=analytics_endpoint,
        )
        print(format_operational_events(events))
        if not events:
            all_errors.append(
                f"[{job_id}] No persisted events returned for {label} run"
            )
            continue
        reasons = {e.get("reason") for e in events}
        for expected_reason in expected_reasons:
            if expected_reason not in reasons:
                all_errors.append(
                    f"[{job_id}] Expected persisted event '{expected_reason}' for "
                    f"{label} run, but it was not found"
                )

    if all_errors:
        print(f"\n{Colors.RED}Job events persistence validation failed:{Colors.RESET}")
        for error in all_errors:
            print(f"{Colors.RED}  - {error}{Colors.RESET}")
        sys.exit(1)

    print(f"\n{Colors.GREEN}✅ Job events persistence validation passed!{Colors.RESET}")


def main():
    parser = argparse.ArgumentParser(
        description="Submit and monitor SQL jobs for cleanroom analytics"
    )
    parser.add_argument(
        "--deployment-config-dir",
        default=None,
        help="Directory containing deployment configuration files (defaults to script_dir/../../workloads/generated)",
    )
    parser.add_argument(
        "--out-dir",
        default=None,
        help="Output directory (defaults to script_dir/generated)",
    )
    parser.add_argument(
        "--parallel", action="store_true", help="Execute SQL tests in parallel"
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Perform a dry run (returns SKU settings without execution)",
    )
    parser.add_argument(
        "--use-optimizer",
        action="store_true",
        help="Use AI optimizer for Spark configuration",
    )
    parser.add_argument(
        "--list",
        action="store_true",
        help="List all available test cases and exit",
    )
    parser.add_argument(
        "--name",
        action="append",
        help="Run only the specified test(s) by name. Can be used multiple times to select multiple tests.",
    )
    parser.add_argument(
        "--formats",
        nargs="+",
        choices=["csv", "json", "parquet"],
        default=["csv"],
        help="Data formats to test (default: csv only)",
    )
    parser.add_argument(
        "--scale-sku",
        choices=["small", "medium", "large"],
        default="small",
        help="Spark scale SKU to use for submitted SQL jobs (default: small)",
    )
    args = parser.parse_args()

    # Define all available tests
    ALL_TESTS = [
        "unapproved-query",
        "publisher-query-runtime-option",
        "publisher-dataset-runtime-option",
        "consumer-query-runtime-option",
        "consumer-dataset-runtime-option",
        "standard-query",
        "s3-query-with-dates",
        "s3-kmin-query-with-dates",
        "low-kmin-query",
        "run-history-validation",
        "job-events-validation",
    ]

    # Handle --list option
    if args.list:
        print("Available test cases:")
        for test in ALL_TESTS:
            print(f"  - {test}")
        sys.exit(0)

    # Determine which tests to run
    if args.name:
        selected_tests = set(args.name)
        # Validate test names
        invalid_tests = selected_tests - set(ALL_TESTS)
        if invalid_tests:
            print(f"Error: Unknown test(s): {', '.join(invalid_tests)}")
            print(f"\nAvailable tests:")
            for test in ALL_TESTS:
                print(f"  - {test}")
            sys.exit(1)
    else:
        # Run all tests if no --name specified
        selected_tests = set(ALL_TESTS)

    script_dir = Path(__file__).parent
    deployment_config_dir = args.deployment_config_dir or str(
        script_dir / ".." / ".." / "workloads" / "generated"
    )
    out_dir = args.out_dir or str(script_dir / "generated")

    # Load job configuration
    config_path = Path(out_dir) / "submitSqlJobConfig.json"
    with open(config_path, "r") as f:
        job_config = json.load(f)

    contract_id = job_config["contractId"]
    queries = job_config["queries"]
    publisher_cgs_client = job_config["publisherCgsClient"]
    consumer_cgs_client = job_config["consumerCgsClient"]
    consumer_output_s3_bucket_name = job_config["consumerOutputS3BucketName"]
    frontend_endpoint = job_config["frontendEndpoint"]
    collaboration_id = job_config["collaborationId"]
    infra_type = job_config["infraType"]
    os.environ["CLEANROOM_COLLABORATION_CONFIG_FILE"] = job_config[
        "collaborationConfigFile"
    ]

    # Start kubectl proxy if analytics endpoint not explicitly provided
    kube_config = str(
        Path(deployment_config_dir) / "cl-cluster" / "k8s-credentials.yaml"
    )

    # Start kubectl proxy on port 8181
    process, proxy_address = start_kubectl_proxy(kube_config)

    analytics_endpoint = (
        f"http://{proxy_address}:{KUBECTL_PROXY_PORT}/api/v1/namespaces/"
        f"cleanroom-spark-analytics-agent/services/"
        f"https:cleanroom-spark-analytics-agent:443/proxy"
    )
    print(f"Using analytics endpoint: {analytics_endpoint}")

    # Wait for analytics endpoint to be ready
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
        except Exception as e:
            pass

        print(
            f"Waiting for analytics endpoint to be ready at {analytics_endpoint}/ready"
        )
        time.sleep(3)
    else:
        raise TimeoutError("Hit timeout waiting for analytics endpoint to be ready.")

    if "unapproved-query" in selected_tests:
        unapproved_query_document_id = queries["csv_malicious"]
        print(
            f"Executing unapproved query '{unapproved_query_document_id}' and expecting failure."
        )
        test_query_invocation(
            query_document_id=unapproved_query_document_id,
            cgs_client=consumer_cgs_client,
            contract_id=contract_id,
            expected_error_code="QueryMissingApprovalsFromDatasetOwners",
            expected_events={"audit": {"Query execution denied": 1}},
            frontend_endpoint=frontend_endpoint,
            collaboration_id=collaboration_id,
            analytics_endpoint=analytics_endpoint,
        )

    csv_query_id = queries["csv_standard"]
    csv_publisher_dataset_name = job_config["publisherDatasets"]["csv_input"]
    publisher_tests = [
        ("publisher-query-runtime-option", csv_query_id),
        ("publisher-dataset-runtime-option", csv_publisher_dataset_name),
    ]

    for test_name, document_id in publisher_tests:
        if test_name not in selected_tests:
            continue

        # Test runtime option disabling for publisher documents
        result = run_command(
            f"az cleanroom governance client show "
            f"--name {publisher_cgs_client} "
            "--query jwtClaims.oid -o tsv"
        )
        disabled_by = result.stdout.strip()

        print(
            f"Checking that disabling execution consent by publisher (id: {disabled_by}) on document '{document_id}' prevents query execution..."
        )
        option = "execution"

        if frontend_endpoint:
            token = get_access_token(publisher_cgs_client)
            url = frontend_url(
                f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
            )
            headers = build_api_headers(token, use_frontend=True)
            requestbody = {"consentAction": "disable"}
            response = requests.put(
                url, headers=headers, json=requestbody, verify=False
            )
            response.raise_for_status()
            status_url = frontend_url(
                f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
            )
            status_response = requests.get(status_url, headers=headers, verify=False)
            status_response.raise_for_status()
            consent_status = status_response.json()
            print(f"Execution consent status: {json.dumps(consent_status, indent=2)}")

            expected_error_message = f"UserDocument runtime option '{option}' for document {document_id} has been disabled by the following approver(s): m[{disabled_by}]."
            try:
                test_query_invocation(
                    query_document_id=csv_query_id,
                    cgs_client=consumer_cgs_client,
                    contract_id=contract_id,
                    expected_error_code="UserDocumentRuntimeOptionDisabled",
                    expected_error_message=expected_error_message,
                    frontend_endpoint=frontend_endpoint,
                    collaboration_id=collaboration_id,
                )
            finally:
                enable_url = frontend_url(
                    f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
                )
                requestbody = {"consentAction": "enable"}
                response = requests.put(
                    enable_url, headers=headers, json=requestbody, verify=False
                )
                response.raise_for_status()
                status_url = frontend_url(
                    f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
                )
                status_response = requests.get(
                    status_url, headers=headers, verify=False
                )
                status_response.raise_for_status()
                consent_status = status_response.json()
                print(
                    f"Execution consent status: {json.dumps(consent_status, indent=2)}"
                )
        else:
            run_command(
                f"az cleanroom governance user-document runtime-option set "
                f"--document-id {document_id} "
                f"--option {option} "
                "--action disable "
                f"--governance-client {publisher_cgs_client}"
            )

            expected_error_message = f"UserDocument runtime option '{option}' for document {document_id} has been disabled by the following approver(s): m[{disabled_by}]."
            test_query_invocation(
                query_document_id=csv_query_id,
                cgs_client=consumer_cgs_client,
                contract_id=contract_id,
                expected_error_code="UserDocumentRuntimeOptionDisabled",
                expected_error_message=expected_error_message,
                analytics_endpoint=analytics_endpoint,
            )

            run_command(
                f"az cleanroom governance user-document runtime-option set "
                f"--document-id {document_id} "
                f"--option {option} "
                "--action enable "
                f"--governance-client {publisher_cgs_client}"
            )

    csv_consumer_dataset_name = job_config["consumerDatasets"]["csv_input"]
    consumer_tests = [
        ("consumer-query-runtime-option", csv_query_id),
        ("consumer-dataset-runtime-option", csv_consumer_dataset_name),
    ]

    for test_name, document_id in consumer_tests:
        if test_name not in selected_tests:
            continue

        # Test runtime option disabling for consumer documents
        result = run_command(
            f"az cleanroom governance client show "
            f"--name {consumer_cgs_client} "
            "--query jwtClaims.oid -o tsv"
        )
        disabled_by = result.stdout.strip()

        print(
            f"Checking that disabling execution consent by consumer (id: {disabled_by}) on document '{document_id}' prevents query execution..."
        )
        option = "execution"
        if frontend_endpoint:
            token = get_access_token(consumer_cgs_client)
            url = frontend_url(
                f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
            )
            headers = build_api_headers(token, use_frontend=True)
            requestbody = {"consentAction": "disable"}
            response = requests.put(
                url, headers=headers, json=requestbody, verify=False
            )
            response.raise_for_status()
            status_url = frontend_url(
                f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
            )
            status_response = requests.get(status_url, headers=headers, verify=False)
            status_response.raise_for_status()
            consent_status = status_response.json()
            print(f"Execution consent status: {json.dumps(consent_status, indent=2)}")

            expected_error_message = f"UserDocument runtime option '{option}' for document {document_id} has been disabled by the following approver(s): m[{disabled_by}]."
            try:
                test_query_invocation(
                    query_document_id=csv_query_id,
                    cgs_client=consumer_cgs_client,
                    contract_id=contract_id,
                    expected_error_code="UserDocumentRuntimeOptionDisabled",
                    expected_error_message=expected_error_message,
                    frontend_endpoint=frontend_endpoint,
                    collaboration_id=collaboration_id,
                )
            finally:
                enable_url = frontend_url(
                    f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
                )
                requestbody = {"consentAction": "enable"}
                response = requests.put(
                    enable_url, headers=headers, json=requestbody, verify=False
                )
                response.raise_for_status()
                status_url = frontend_url(
                    f"{frontend_endpoint}/collaborations/{collaboration_id}/consent/{document_id}"
                )
                status_response = requests.get(
                    status_url, headers=headers, verify=False
                )
                status_response.raise_for_status()
                consent_status = status_response.json()
                print(
                    f"Execution consent status: {json.dumps(consent_status, indent=2)}"
                )
        else:
            run_command(
                f"az cleanroom governance user-document runtime-option set "
                f"--document-id {document_id} "
                f"--option {option} "
                "--action disable "
                f"--governance-client {consumer_cgs_client}"
            )

            expected_error_message = f"UserDocument runtime option '{option}' for document {document_id} has been disabled by the following approver(s): m[{disabled_by}]."
            test_query_invocation(
                query_document_id=csv_query_id,
                cgs_client=consumer_cgs_client,
                contract_id=contract_id,
                expected_error_code="UserDocumentRuntimeOptionDisabled",
                expected_error_message=expected_error_message,
                analytics_endpoint=analytics_endpoint,
            )

            run_command(
                f"az cleanroom governance user-document runtime-option set "
                f"--document-id {document_id} "
                f"--option {option} "
                "--action enable "
                f"--governance-client {consumer_cgs_client}"
            )

    test_cases_map = {}

    # Define SQL test cases
    test_cases_map["standard-query"] = [
        {
            "name": f"Standard Query ({format_name.upper()})",
            "query_document_id": queries[f"{format_name}_standard"],
            "start_date": None,
            "end_date": None,
            "expect_failure": False,
            "expected_events": {
                "operational": {
                    "DATASET_LOAD_STARTED": 2,
                    "DATASET_LOAD_COMPLETED": 2,
                    "QUERY_SEGMENT_EXECUTION_STARTED": 3,
                    "QUERY_SEGMENT_EXECUTION_COMPLETED": 3,
                    "DATASET_WRITE_STARTED": 1,
                    "DATASET_WRITE_COMPLETED": 1,
                    "QUERY_EXECUTION_COMPLETED": 1,
                    "QUERY_STATISTICS": 1,
                },
                "audit": {
                    "DATASET_LOAD_COMPLETED": 2,
                    "DATASET_WRITE_COMPLETED": 1,
                    "QUERY_COMPLETED": 1,
                },
            },
            "dry_run": args.dry_run,
            "use_optimizer": args.use_optimizer,
            "scale_sku": args.scale_sku,
        }
        for format_name in args.formats
    ]

    test_cases_map["s3-query-with-dates"] = [
        {
            "name": f"S3 Query ({format_name.upper()})",
            "query_document_id": queries[f"s3_{format_name}_standard"],
            "start_date": "2025-09-01T00:00:00+00:00",
            "end_date": "2025-09-02T00:00:00+00:00",
            "expect_failure": False,
            "expected_events": {
                "operational": {
                    "DATASET_LOAD_STARTED": 2,
                    "DATASET_LOAD_COMPLETED": 2,
                    "QUERY_SEGMENT_EXECUTION_STARTED": 3,
                    "QUERY_SEGMENT_EXECUTION_COMPLETED": 3,
                    "DATASET_WRITE_STARTED": 1,
                    "DATASET_WRITE_COMPLETED": 1,
                    "QUERY_EXECUTION_COMPLETED": 1,
                    "QUERY_STATISTICS": 1,
                },
                "audit": {
                    "DATASET_LOAD_COMPLETED": 2,
                    "DATASET_WRITE_COMPLETED": 1,
                    "QUERY_COMPLETED": 1,
                },
            },
            "validate_output": {
                "output_datastore_type": "s3",
                "dst_dir": f"{out_dir}/s3queryOutput/{format_name}",
                "datastore_name": f"consumer-output-s3-{format_name}",
                "output_format": format_name,
                "expected_row_count": 12,
                "bucket_name": f"{consumer_output_s3_bucket_name}-{format_name}",
            },
            "dry_run": args.dry_run,
            "use_optimizer": args.use_optimizer,
            "scale_sku": args.scale_sku,
        }
        for format_name in args.formats
    ]

    # S3 Kmin query for csv.
    test_cases_map["s3-kmin-query-with-dates"] = [
        {
            "name": f"S3 Kmin Query (CSV)",
            "query_document_id": queries[f"s3_csv_kmin"],
            "start_date": "2025-09-01T00:00:00+00:00",
            "end_date": "2025-09-02T00:00:00+00:00",
            "expect_failure": False,
            "expected_events": {
                "operational": {
                    "DATASET_LOAD_STARTED": 2,
                    "DATASET_LOAD_COMPLETED": 2,
                    "QUERY_SEGMENT_EXECUTION_STARTED": 3,
                    "QUERY_SEGMENT_EXECUTION_COMPLETED": 3,
                    "DATASET_WRITE_STARTED": 1,
                    "DATASET_WRITE_COMPLETED": 1,
                    "QUERY_EXECUTION_COMPLETED": 1,
                    "QUERY_STATISTICS": 1,
                },
                "audit": {
                    "DATASET_LOAD_COMPLETED": 2,
                    "DATASET_WRITE_COMPLETED": 1,
                    "QUERY_COMPLETED": 1,
                },
            },
            "validate_output": {
                "output_datastore_type": "s3",
                "dst_dir": f"{out_dir}/s3queryOutput/csv",
                "datastore_name": "consumer-output-s3-csv",
                "output_format": "csv",
                "expected_row_count": 5,
                "bucket_name": f"{consumer_output_s3_bucket_name}-csv",
            },
            "dry_run": args.dry_run,
            "use_optimizer": args.use_optimizer,
            # Use just small in this e2e test: the medium executor (5 CPU / 27.9 GB
            # group) fails to schedule in several regions (CACI capacity -> the job
            # hangs in RUNNING until the status-check timeout), and Virtual Kind
            # workers can't fit a medium executor on one worker either. The
            # scale/stress suite exercises all SKUs (small/medium/large).
            "scale_sku": "small",
        }
    ]

    # Low Kmin queries (expected failure) for csv.
    test_cases_map["low-kmin-query"] = [
        {
            "name": f"Low Kmin Query (CSV, Expected Failure)",
            "query_document_id": queries["csv_lowkmin"],
            "start_date": None,
            "end_date": None,
            "expect_failure": True,
            "expected_events": {
                "operational": {
                    "DATASET_LOAD_STARTED": 2,
                    "DATASET_LOAD_COMPLETED": 2,
                    "QUERY_SEGMENT_EXECUTION_STARTED": 3,
                    "QUERY_SEGMENT_EXECUTION_COMPLETED": 2,
                    "QUERY_SEGMENT_EXECUTION_FAILED": 1,
                    "QUERY_EXECUTION_FAILED": 1,
                },
                "audit": {
                    "DATASET_LOAD_COMPLETED": 2,
                    "QUERY_FAILED": 1,
                },
            },
            "dry_run": args.dry_run,
            "use_optimizer": args.use_optimizer,
            "scale_sku": args.scale_sku,
        }
    ]

    # Filter test cases based on selected tests
    test_cases = [
        test_case
        for test_id in test_cases_map.keys()
        if test_id in selected_tests
        for test_case in test_cases_map[test_id]
    ]

    print("=== SQL Job Test Cases to Execute ===")
    for test_case in test_cases:
        print(f" - {test_case['name']}")
    if not frontend_endpoint:
        collaboration_context = consumer_cgs_client
        print(f"Setting collaboration context to '{collaboration_context}'")
        run_command(
            f"az cleanroom collaboration context set --collaboration-name {collaboration_context}"
        )
    if args.parallel:
        # Execute tests in parallel
        print(f"Executing {len(test_cases)} SQL job tests in parallel...")

        with ThreadPoolExecutor(max_workers=MAX_PARALLEL_SQL_JOBS) as executor:
            futures = []
            for test_case in test_cases:
                future = executor.submit(
                    execute_sql_test_parallel,
                    test_name=test_case["name"],
                    query_document_id=test_case["query_document_id"],
                    contract_id=contract_id,
                    kube_config=kube_config,
                    start_date=test_case["start_date"],
                    end_date=test_case["end_date"],
                    scale_sku=test_case["scale_sku"],
                    expect_failure=test_case["expect_failure"],
                    expected_events=test_case.get("expected_events"),
                    validate_output=test_case.get("validate_output"),
                    dry_run=test_case["dry_run"],
                    use_optimizer=test_case["use_optimizer"],
                    frontend_endpoint=frontend_endpoint,
                    collaboration_id=collaboration_id,
                    cgs_client=consumer_cgs_client,
                )
                futures.append(future)

            # Wait for all to complete and collect results
            results = []
            for future in as_completed(futures):
                results.append(future.result())
    else:
        # Execute tests sequentially
        results = []
        for test_case in test_cases:
            result = execute_sql_test_parallel(
                test_name=test_case["name"],
                query_document_id=test_case["query_document_id"],
                contract_id=contract_id,
                kube_config=kube_config,
                start_date=test_case["start_date"],
                end_date=test_case["end_date"],
                scale_sku=test_case["scale_sku"],
                expect_failure=test_case["expect_failure"],
                expected_events=test_case.get("expected_events"),
                validate_output=test_case.get("validate_output"),
                dry_run=test_case["dry_run"],
                use_optimizer=test_case["use_optimizer"],
                frontend_endpoint=frontend_endpoint,
                collaboration_id=collaboration_id,
                cgs_client=consumer_cgs_client,
            )
            results.append(result)

    # Print test results summary
    print("\n=== Test Results Summary ===")
    failed_tests = []
    for result in results:
        duration_str = f" ({result['duration']:.2f}s)"
        if result["success"]:
            print(
                f"{Colors.GREEN}✓ {result['name']}: PASSED{duration_str}. Job ID: {result.get('job_id', 'N/A')}{Colors.RESET}"
            )
        else:
            print(
                f"{Colors.RED}✗ {result['name']}: FAILED{duration_str}. - {result['error']}. Job ID: {result.get('job_id', 'N/A')}{Colors.RESET}"
            )
            failed_tests.append(result)

    if failed_tests:
        print(
            f"\n{Colors.RED}❌ {len(failed_tests)} out of {len(results)} tests failed.{Colors.RESET}"
        )
        sys.exit(1)

        print(
            f"\n{Colors.GREEN}✅ All {len(results)} tests passed successfully!{Colors.RESET}"
        )

    # Run history (JobRecord CRD) and job events (JobEventRecord CRD) validation
    # tests. Both need multiple seeded runs of the same successful/failing
    # queries, so seed once and share the resulting job ids.
    run_history_selected = "run-history-validation" in selected_tests
    job_events_selected = "job-events-validation" in selected_tests
    if run_history_selected or job_events_selected:
        print("\n=== Preparing Runs for Run History / Job Events Validation ===")
        seed_cases = [
            {
                "name": "Seed - Successful (CSV Standard)",
                "query_document_id": queries["csv_standard"],
                "expect_failure": False,
            },
            {
                "name": "Seed - Failing (CSV Low Kmin)",
                "query_document_id": queries["csv_lowkmin"],
                "expect_failure": True,
            },
        ]

        # Job ids per query, oldest first, used to prove that runs/events
        # persist for prior (non-latest) runs.
        successful_job_ids: list[str] = []
        failing_job_ids: list[str] = []

        def _record_seed(seed_result: Dict[str, Any]) -> None:
            if not seed_result["success"]:
                print(
                    f"{Colors.RED}Seed execution failed: {seed_result['error']}{Colors.RESET}"
                )
                sys.exit(1)
            job_id = seed_result.get("job_id")
            if not job_id:
                return
            if seed_result["query_document_id"] == queries["csv_lowkmin"]:
                failing_job_ids.append(job_id)
            else:
                successful_job_ids.append(job_id)

        for run_number in range(1, RUN_HISTORY_MIN_EXPECTED_RUNS + 1):
            print(
                f"\nExecuting seed pass {run_number}/{RUN_HISTORY_MIN_EXPECTED_RUNS}..."
            )
            if args.parallel:
                with ThreadPoolExecutor(max_workers=len(seed_cases)) as executor:
                    futures = [
                        executor.submit(
                            execute_sql_test_parallel,
                            test_name=(
                                f"{seed_case['name']} "
                                f"[run {run_number}/{RUN_HISTORY_MIN_EXPECTED_RUNS}]"
                            ),
                            query_document_id=seed_case["query_document_id"],
                            contract_id=contract_id,
                            kube_config=kube_config,
                            expect_failure=seed_case["expect_failure"],
                            dry_run=args.dry_run,
                            use_optimizer=args.use_optimizer,
                            frontend_endpoint=frontend_endpoint,
                            collaboration_id=collaboration_id,
                            cgs_client=consumer_cgs_client,
                            scale_sku=args.scale_sku,
                        )
                        for seed_case in seed_cases
                    ]
                    for future in as_completed(futures):
                        _record_seed(future.result())
            else:
                for seed_case in seed_cases:
                    _record_seed(
                        execute_sql_test_parallel(
                            test_name=(
                                f"{seed_case['name']} "
                                f"[run {run_number}/{RUN_HISTORY_MIN_EXPECTED_RUNS}]"
                            ),
                            query_document_id=seed_case["query_document_id"],
                            contract_id=contract_id,
                            kube_config=kube_config,
                            expect_failure=seed_case["expect_failure"],
                            dry_run=args.dry_run,
                            use_optimizer=args.use_optimizer,
                            frontend_endpoint=frontend_endpoint,
                            collaboration_id=collaboration_id,
                            cgs_client=consumer_cgs_client,
                            scale_sku=args.scale_sku,
                        )
                    )

        if run_history_selected:
            run_history_validation_test(
                successful_query_id=queries["csv_standard"],
                failing_query_id=queries["csv_lowkmin"],
                cgs_client=consumer_cgs_client,
                frontend_endpoint=frontend_endpoint,
                collaboration_id=collaboration_id,
                analytics_endpoint=analytics_endpoint,
            )

        if job_events_selected:
            job_events_validation_test(
                successful_job_ids=successful_job_ids,
                failing_job_ids=failing_job_ids,
                cgs_client=consumer_cgs_client,
                frontend_endpoint=frontend_endpoint,
                collaboration_id=collaboration_id,
                analytics_endpoint=analytics_endpoint,
            )


if __name__ == "__main__":
    try:
        main()
    except Exception as e:
        print_kubectl_proxy_log()
        raise
