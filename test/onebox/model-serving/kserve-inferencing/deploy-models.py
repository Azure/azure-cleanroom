#!/usr/bin/env python3
"""
Deploy KServe Models Script

This script deploys example models to a KServe inferencing service endpoint,
setting up a kubectl proxy and polling for deployment completion.
"""

import argparse
import atexit
import json
import signal
import socket
import subprocess
import sys
import time
import traceback
import uuid
from datetime import datetime, timedelta
from pathlib import Path

import requests
import urllib3

# --- Constants ---
KUBECTL_PROXY_PORT = 8282
HEADER_AUTHZ = "x-ms-cleanroom-authorization"
HEADER_CORRELATION_ID = "x-ms-correlation-id"
HEADER_CLIENT_REQUEST_ID = "x-ms-client-request-id"


# Suppress InsecureRequestWarning for verify=False HTTPS calls via port-forward.
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)


# Import shared utilities
_git_root = subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
).stdout.strip()
sys.path.insert(0, str(Path(_git_root) / "test" / "onebox"))
from cleanroom_test_utils import Colors, run_command

# Global variable to track kubectl proxy process
_kubectl_proxy_process: subprocess.Popen | None = None


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


def start_kubectl_proxy(kube_config: str, port: int = KUBECTL_PROXY_PORT) -> None:
    """
    Start kubectl proxy and verify it started successfully.

    Args:
        kube_config: Path to kubeconfig file
        port: Port to use for kubectl proxy

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

    process = subprocess.Popen(
        ["kubectl", "proxy", "--port", str(port), "--kubeconfig", kube_config],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )

    _kubectl_proxy_process = process

    # Check process didn't exit immediately
    time.sleep(0.5)
    if process.poll() is not None:
        stdout, stderr = process.communicate()
        raise RuntimeError(
            f"kubectl proxy exited unexpectedly.\nStdout: {stdout}\nStderr: {stderr}"
        )

    try:
        _wait_for_port(port, timeout=30)
    except RuntimeError:
        process.terminate()
        process.wait()
        raise RuntimeError(f"kubectl proxy failed to start within 30 seconds")

    print(
        f"{Colors.GREEN}kubectl proxy started successfully on port {port}{Colors.RESET}"
    )


def get_timestamp():
    """Return formatted timestamp for logging"""
    now = datetime.now()
    return f"[{now.strftime('%m/%d/%y')} {now.strftime('%H:%M:%S')}]"


def wait_for_endpoint_ready(endpoint: str, timeout_minutes: int = 1):
    """
    Wait for the endpoint to be /ready.

    Args:
        endpoint: The endpoint URL to check
        timeout_minutes: Maximum time to wait in minutes

    Raises:
        TimeoutError: If endpoint doesn't become ready within timeout
    """
    timeout = timedelta(minutes=timeout_minutes)
    start_time = datetime.now()

    while True:
        # Check endpoint readiness
        try:
            response = requests.get(f"{endpoint}/ready", verify=False, timeout=10)
            if response.status_code == 200:
                print(f"{Colors.GREEN}{endpoint} endpoint is ready{Colors.RESET}")
                return
        except Exception:
            pass

        print(f"Waiting for endpoint to be ready at {endpoint}/ready")
        time.sleep(3)

        if datetime.now() - start_time > timeout:
            raise TimeoutError("Hit timeout waiting for endpoint to be ready.")


def get_access_token(cgs_client: str) -> str:
    """
    Get access token from Azure cleanroom governance client.

    Args:
        cgs_client: Name of the cleanroom governance client

    Returns:
        Access token string
    """
    result = run_command(
        f"az cleanroom governance client get-access-token --query accessToken -o tsv --name {cgs_client}"
    )
    return result.stdout.strip()


def cleanup_old_model_deployment(
    kube_config: str,
    model_name: str,
    namespace: str = "kserve-inferencing",
) -> None:
    """
    Delete any existing InferenceService instance of the model.

    Args:
        kube_config: Path to kubeconfig file
        model_name: Name of the model to clean up
        namespace: Kubernetes namespace of the InferenceService
    """
    kc = f"--kubeconfig {kube_config}"
    print(
        f"{Colors.YELLOW}Checking for existing InferenceService "
        f"'{model_name}' in namespace '{namespace}'...{Colors.RESET}"
    )

    try:
        run_command(f"kubectl {kc} get inferenceservice {model_name} -n {namespace}")
    except subprocess.CalledProcessError as e:
        if "NotFound" in (e.stderr or ""):
            print(
                f"{Colors.GREEN}No existing InferenceService '{model_name}' found. "
                f"Nothing to clean up.{Colors.RESET}"
            )
            return
        raise

    print(
        f"{Colors.YELLOW}Deleting existing InferenceService "
        f"'{model_name}'...{Colors.RESET}"
    )
    try:
        run_command(f"kubectl {kc} delete inferenceservice {model_name} -n {namespace}")
        print(
            f"{Colors.GREEN}Successfully deleted InferenceService "
            f"'{model_name}'.{Colors.RESET}"
        )
    except Exception as e:
        print(
            f"{Colors.YELLOW}Warning: Failed to delete existing "
            f"InferenceService '{model_name}': {e}{Colors.RESET}"
        )


def submit_model_deployment(
    endpoint: str, token: str, model_name: str, correlation_id: str, body: dict
) -> dict:
    """
    Submit a model deployment request.

    Args:
        endpoint: The inferencing endpoint URL
        token: Access token for authentication
        model_name: Name of the model to deploy
        correlation_id: Correlation ID for request tracking
        body: Request body dict

    Returns:
        JSON response from the submission

    Raises:
        RuntimeError: If submission fails
    """
    client_request_id = str(uuid.uuid4())
    print(
        f"Submitting model correlationId: {correlation_id}, clientRequestId: {client_request_id}"
    )

    url = f"{endpoint}/inferenceServices"
    headers = {
        "Content-Type": "application/json",
        HEADER_AUTHZ: f"Bearer {token}",
        HEADER_CORRELATION_ID: correlation_id,
        HEADER_CLIENT_REQUEST_ID: client_request_id,
    }

    try:
        response = requests.post(
            url, json=body, headers=headers, verify=False, timeout=60
        )
        response.raise_for_status()
        return response.json()
    except requests.exceptions.HTTPError as e:
        # Pretty print error response
        try:
            error_json = e.response.json()
            print(json.dumps(error_json, indent=2))
        except (json.JSONDecodeError, ValueError):
            print(e.response.text)
        raise RuntimeError(
            f"/inferenceServices for {model_name} failed. Check the output above for details."
        )


def get_job_status(
    endpoint: str, token: str, model_name: str, correlation_id: str
) -> dict:
    """
    Get the status of a model deployment.

    Args:
        endpoint: The inferencing endpoint URL
        token: Access token for authentication
        model_name: Name of the model
        correlation_id: Correlation ID for request tracking

    Returns:
        JSON response with job status

    Raises:
        RuntimeError: If status check fails
    """
    client_request_id = str(uuid.uuid4())
    print(
        f"Getting job status with correlationId: {correlation_id}, clientRequestId: {client_request_id}"
    )

    url = f"{endpoint}/inferenceServices/{model_name}/status"
    headers = {
        HEADER_AUTHZ: f"Bearer {token}",
        HEADER_CORRELATION_ID: correlation_id,
        HEADER_CLIENT_REQUEST_ID: client_request_id,
    }

    try:
        response = requests.get(url, headers=headers, verify=False, timeout=60)
        response.raise_for_status()
        return response.json()
    except requests.exceptions.HTTPError as e:
        # Pretty print error response
        try:
            error_json = e.response.json()
            print(json.dumps(error_json, indent=2))
        except (json.JSONDecodeError, ValueError):
            print(e.response.text)
        raise RuntimeError(
            f"/inferenceServices/{model_name}/status failed. "
            "Check the output above for details."
        )


def wait_for_deployment(
    endpoint: str,
    cgs_client: str,
    model_name: str,
    correlation_id: str,
    timeout_minutes: int = 30,
):
    """
    Wait for model deployment to complete.

    Args:
        endpoint: The inferencing endpoint URL
        cgs_client: Name of the cleanroom governance client
        model_name: Name of the model being deployed
        correlation_id: Correlation ID for request tracking
        timeout_minutes: Maximum time to wait in minutes

    Raises:
        TimeoutError: If deployment doesn't complete within timeout
    """
    timeout = timedelta(minutes=timeout_minutes)
    start_time = datetime.now()

    print("Waiting for model deployment to complete...")

    while True:
        print(
            f"{get_timestamp()} Checking status of inferencing service for {model_name}"
        )

        token = get_access_token(cgs_client)
        job_status = get_job_status(endpoint, token, model_name, correlation_id)

        # Pretty print status
        print(json.dumps(job_status, indent=2))

        # Check if deployment is complete: URL must be present AND the
        # Ready condition (if reported) must be True.  This prevents
        # returning early when --no-delete is used and a rolling update
        # is still in progress (the old URL is still present but the
        # Ready condition transitions to Unknown/False).
        status = job_status.get("status", {})
        url = status.get("url")
        if url and _is_ready_condition_met(status):
            print(
                f"{Colors.GREEN}{get_timestamp()} Model has completed deployment.{Colors.RESET}"
            )
            return

        if datetime.now() - start_time > timeout:
            raise TimeoutError(
                f"Hit timeout waiting for model {model_name} to complete deployment."
            )

        print("Waiting for 10 seconds before checking status again...")
        time.sleep(10)


def _is_ready_condition_met(status: dict) -> bool:
    """
    Check if the KServe Ready condition is True.

    Returns True if:
    - The Ready condition exists and its status is "True", or
    - No conditions are reported yet (early in a fresh deployment).
    """
    conditions = status.get("conditions") or []
    ready = next((c for c in conditions if c.get("type") == "Ready"), None)
    if ready is None:
        return True
    return ready.get("status") == "True"


def verify_predictor_deployment_spec(
    kube_config: str,
    model_name: str,
    namespace: str = "kserve-inferencing",
    expected_replicas: int = 2,
    expected_resources: dict | None = None,
    expected_args: list | None = None,
    expected_env: dict | None = None,
) -> None:
    """
    Verify that KServe created the predictor deployment with the correct spec.

    Checks that the fields we passed through the CRD are reflected in the
    actual Kubernetes deployment created by KServe.
    """
    kc = f"--kubeconfig {kube_config}"
    deployment_name = f"{model_name}-predictor"
    print(
        f"{Colors.CYAN}Verifying predictor deployment spec for "
        f"'{deployment_name}'...{Colors.RESET}"
    )
    result = run_command(
        f"kubectl {kc} get deployment {deployment_name} -n {namespace} -o json"
    )
    deployment = json.loads(result.stdout)
    spec = deployment["spec"]
    pod_spec = spec["template"]["spec"]

    # Verify replica count.
    actual_replicas = spec.get("replicas", 1)
    if actual_replicas != expected_replicas:
        raise RuntimeError(
            f"Expected {expected_replicas} replicas, got {actual_replicas}"
        )
    print(f"{Colors.GREEN}  ✓ Replicas: {actual_replicas}{Colors.RESET}")

    # Find the serving container (kserve-container or first non-init).
    containers = pod_spec.get("containers", [])
    serving_container = None
    for c in containers:
        if c["name"] == "kserve-container":
            serving_container = c
            break
    if not serving_container and containers:
        serving_container = containers[0]

    if not serving_container:
        raise RuntimeError("No serving container found in deployment")

    # Verify resources if expected.
    if expected_resources:
        actual_resources = serving_container.get("resources", {})
        for category in ["requests", "limits"]:
            if category in expected_resources:
                actual = actual_resources.get(category, {})
                for key, value in expected_resources[category].items():
                    if actual.get(key) != value:
                        raise RuntimeError(
                            f"Expected resource {category}.{key}={value}, "
                            f"got {actual.get(key)}"
                        )
        print(f"{Colors.GREEN}  ✓ Resources match expected spec.{Colors.RESET}")

    # Verify args if expected.
    if expected_args:
        actual_args = serving_container.get("args", [])
        for arg in expected_args:
            if not any(arg in a for a in actual_args):
                raise RuntimeError(
                    f"Expected arg '{arg}' not found in container args: {actual_args}"
                )
        print(f"{Colors.GREEN}  ✓ Args contain expected values.{Colors.RESET}")

    # Verify env vars if expected.
    if expected_env:
        actual_env = {
            e["name"]: e.get("value", "") for e in serving_container.get("env", [])
        }
        for name, value in expected_env.items():
            if actual_env.get(name) != value:
                raise RuntimeError(
                    f"Expected env {name}={value}, got {actual_env.get(name)}"
                )
        print(f"{Colors.GREEN}  ✓ Env vars match expected values.{Colors.RESET}")

    # Verify deployment strategy if present.
    strategy = spec.get("strategy", {})
    if strategy:
        strategy_type = strategy.get("type", "")
        print(f"{Colors.GREEN}  ✓ Deployment strategy: {strategy_type}{Colors.RESET}")

    print(
        f"{Colors.GREEN}  Predictor deployment spec verification passed.{Colors.RESET}"
    )


def verify_predictor_deployment_placement(
    kube_config: str,
    model_name: str,
    namespace: str = "kserve-inferencing",
) -> None:
    """
    Verify predictor deployment placement based on node type.

    For flexnode deployments, checks that the pod template has:
    - Annotations: api-server-proxy.io/policy, api-server-proxy.io/signature
    - nodeSelector: pod-policy=required

    Args:
        kube_config: Path to kubeconfig file
        namespace: Kubernetes namespace of the deployment

    Raises:
        RuntimeError: If placement verification fails
    """
    kc = f"--kubeconfig {kube_config}"
    deployment_name = f"{model_name}-predictor"
    print(
        f"{Colors.CYAN}Verifying predictor placement on deployment "
        f"'{deployment_name}'...{Colors.RESET}"
    )
    result = run_command(
        f"kubectl {kc} get deployment {deployment_name} -n {namespace} -o json"
    )
    deployment = json.loads(result.stdout)
    pod_template = deployment["spec"]["template"]

    pod_annotations = pod_template.get("metadata", {}).get("annotations", {})
    flex_node_annotations = [
        "api-server-proxy.io/policy",
        "api-server-proxy.io/signature",
    ]
    node_selector = pod_template.get("spec", {}).get("nodeSelector", {})

    # Verify annotations are present.
    for annotation in flex_node_annotations:
        if annotation not in pod_annotations:
            raise RuntimeError(
                f"Deployment '{deployment_name}' is missing required annotation "
                f"'{annotation}'. Annotations found: {pod_annotations}"
            )
    print(
        f"{Colors.GREEN}  ✓ Annotations "
        f"{flex_node_annotations} are present.{Colors.RESET}"
    )

    # Verify nodeSelector is set.
    if node_selector.get("pod-policy") != "required":
        raise RuntimeError(
            f"Deployment '{deployment_name}' does not have expected "
            f"nodeSelector 'pod-policy: required'. "
            f"nodeSelector found: {node_selector}"
        )
    print(
        f"{Colors.GREEN}  ✓ nodeSelector 'pod-policy: required' is set.{Colors.RESET}"
    )

    # Verify GPU deployments have GFD node affinity.
    containers = pod_template.get("spec", {}).get("containers", [])
    has_gpu = any(
        "nvidia.com/gpu"
        in {
            **(c.get("resources", {}).get("requests", {}) or {}),
            **(c.get("resources", {}).get("limits", {}) or {}),
        }
        for c in containers
    )
    if has_gpu:
        affinity = pod_template.get("spec", {}).get("affinity", {})
        node_affinity = affinity.get("nodeAffinity", {})
        required = node_affinity.get(
            "requiredDuringSchedulingIgnoredDuringExecution", {}
        )
        terms = required.get("nodeSelectorTerms", [])
        gfd_found = any(
            expr.get("key") == "nvidia.com/gpu.product"
            and expr.get("operator") == "Exists"
            for term in terms
            for expr in term.get("matchExpressions", [])
        )
        if not gfd_found:
            raise RuntimeError(
                f"GPU deployment '{deployment_name}' is missing required "
                f"node affinity for GFD label 'nvidia.com/gpu.product'. "
                f"affinity found: {affinity}"
            )
        print(
            f"{Colors.GREEN}  ✓ nodeAffinity requires GFD label "
            f"'nvidia.com/gpu.product'.{Colors.RESET}"
        )


def verify_all_replicas_functional(
    kube_config: str,
    model_name: str,
    payload: dict,
    extract_result,
    namespace: str = "kserve-inferencing",
    base_port: int = 9500,
    expected_count: int | None = None,
) -> None:
    """Verify each predictor pod can serve inference by port-forwarding to it.

    Iterates over all pods backing the predictor deployment and sends a
    single inference request to each one to confirm it is functional.

    Args:
        expected_count: If set, asserts that at least this many pods exist
            before testing. Fails early if fewer pods are found.
    """
    kc = f"--kubeconfig {kube_config}"
    deployment_name = f"{model_name}-predictor"
    label_selector = f"serving.kserve.io/inferenceservice={model_name}"
    result = run_command(
        f"kubectl {kc} get pods -n {namespace} -l {label_selector} "
        f"-o jsonpath={{.items[*].metadata.name}}"
    )
    pod_names = result.stdout.strip().split()
    if not pod_names:
        raise RuntimeError(f"No pods found for deployment '{deployment_name}'.")
    if expected_count is not None and len(pod_names) < expected_count:
        raise RuntimeError(
            f"Expected at least {expected_count} pods for "
            f"'{deployment_name}', found {len(pod_names)}."
        )
    print(
        f"{Colors.CYAN}Verifying all {len(pod_names)} replica(s) of "
        f"'{model_name}' are functional...{Colors.RESET}"
    )
    inference_path = "/v1/chat/completions"
    for i, pod_name in enumerate(pod_names):
        # Wait for pod readiness before port-forwarding. Init containers
        # (attestation, governance, blobfuse) can take minutes; the
        # port-forward timeout should only cover the forward itself.
        print(
            f"{Colors.CYAN}  Waiting for pod '{pod_name}' to be ready...{Colors.RESET}"
        )
        run_command(
            f"kubectl {kc} wait --for=condition=Ready "
            f"pod/{pod_name} -n {namespace} --timeout=300s"
        )
        port = base_port + i
        pf = subprocess.Popen(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "port-forward",
                f"pod/{pod_name}",
                f"--namespace={namespace}",
                f"{port}:8080",
            ],
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )
        try:
            _wait_for_port(port)
            url = f"http://localhost:{port}{inference_path}"
            resp = requests.post(
                url,
                json=payload,
                headers={"Content-Type": "application/json"},
                timeout=60,
            )
            resp.raise_for_status()
            text = extract_result(resp.json())
            print(
                f"{Colors.GREEN}  ✓ Pod '{pod_name}' responded: "
                f"{text[:80]}{Colors.RESET}"
            )
        except Exception as exc:
            raise RuntimeError(f"Pod '{pod_name}' failed inference: {exc}") from exc
        finally:
            pf.terminate()
            try:
                pf.wait(timeout=5)
            except subprocess.TimeoutExpired:
                pf.kill()
                pf.wait()
    print(
        f"{Colors.GREEN}All {len(pod_names)} replica(s) of "
        f"'{model_name}' are functional.{Colors.RESET}"
    )


def test_inferencing_via_port_forward_to_predictor_svc(
    kube_config: str,
    model_name: str,
    inference_path: str,
    payload: dict,
    extract_result,
    namespace: str = "kserve-inferencing",
    port: int = 8989,
):
    """Test a deployed model by port-forwarding and sending an inference request.

    Port-forwards to the predictor HTTPS service and sends an inference request.

    Args:
        kube_config: Path to kubeconfig file.
        model_name: Name of the deployed InferenceService.
        inference_path: URL path for inference (e.g. /v2/models/{name}/infer).
        payload: JSON payload to send.
        extract_result: Callable(response_json) -> str to extract display text.
        namespace: Kubernetes namespace.
        port: Local port for port-forwarding.
    """
    predictor_svc = f"{model_name}-predictor-https"
    print(
        f"{Colors.CYAN}Starting port-forward to predictor service "
        f"{predictor_svc} in namespace {namespace} on port {port}..."
        f"{Colors.RESET}"
    )
    port_forward_process = subprocess.Popen(
        [
            "kubectl",
            "--kubeconfig",
            kube_config,
            "port-forward",
            f"svc/{predictor_svc}",
            f"--namespace={namespace}",
            f"{port}:443",
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )

    try:
        _wait_for_port(port)

        inference_url = f"https://localhost:{port}{inference_path}"
        max_retries = 10
        retry_delay = 5
        for attempt in range(1, max_retries + 1):
            response = requests.post(
                inference_url,
                json=payload,
                headers={"Content-Type": "application/json"},
                timeout=60,
                verify=False,
            )
            if response.status_code != 503:
                try:
                    response.raise_for_status()
                except requests.exceptions.HTTPError:
                    print(f"{Colors.RED}Response body: {response.text}{Colors.RESET}")
                    raise
                break
            if attempt < max_retries:
                print(
                    f"{Colors.YELLOW}Attempt {attempt}/{max_retries} failed "
                    f"({response.status_code}). Retrying in {retry_delay}s..."
                    f"{Colors.RESET}"
                )
                time.sleep(retry_delay)
            else:
                try:
                    response.raise_for_status()
                except requests.exceptions.HTTPError:
                    print(f"{Colors.RED}Response body: {response.text}{Colors.RESET}")
                    raise

        result_text = extract_result(response.json())
        print(f"{Colors.GREEN}Inference response: {result_text}{Colors.RESET}")
        print(f"{Colors.GREEN}Model '{model_name}' test passed!{Colors.RESET}")
    finally:
        port_forward_process.terminate()
        try:
            port_forward_process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            port_forward_process.kill()
            port_forward_process.wait()


# Type alias for the test results matrix: maps (model, mode) -> (passed, error).
TestResults = dict[tuple[str, str], tuple[bool, str]]


def test_inference(
    kube_config: str,
    model_name: str,
    inference_path: str,
    payload: dict,
    extract_result,
    ca_cert: str,
    cgs_client: str,
    inferencing_agent_endpoint: str,
    ohttp_client_image: str,
    test_results: TestResults,
    namespace: str = "kserve-inferencing",
    port: int = 8989,
    timeout_seconds: int = 300,
    test_modes: set | None = None,
):
    """Test a deployed model using the specified test modes.

    Each mode is executed independently so that a failure in one mode does not
    prevent the remaining modes from running.  Results are recorded in
    ``test_results`` keyed by ``(model_name, mode)``.

    Args:
        kube_config: Path to kubeconfig file.
        model_name: Name of the deployed InferenceService.
        inference_path: URL path for inference (e.g. /v2/models/{name}/infer).
        payload: JSON payload to send.
        extract_result: Callable(response_json) -> str to extract display text.
        ca_cert: Path to the cleanroom CA certificate file.
        cgs_client: Name of the cleanroom governance client for token retrieval.
        ohttp_client_image: Docker image for the ohttp-client container.
        test_results: Mutable dict to record per-mode pass/fail results.
        test_modes: Set of test modes to run (predictor, inferencing-agent, ohttp).
    """
    if test_modes is None:
        test_modes = {"predictor", "inferencing-agent", "ohttp"}

    print(f"\n{Colors.CYAN}Testing '{model_name}' in '{namespace}'...{Colors.RESET}")

    kc = f"--kubeconfig {kube_config}"
    run_command(
        f"kubectl {kc} wait --for=condition=Ready "
        f"inferenceservice/{model_name} "
        f"-n {namespace} --timeout={timeout_seconds}s",
    )
    print(f"{Colors.GREEN}InferenceService '{model_name}' is ready!{Colors.RESET}")

    verify_predictor_deployment_placement(kube_config, model_name, namespace)

    # Map mode names to the callables that implement them.
    from collections.abc import Callable

    mode_runners: dict[str, Callable[[], None]] = {}

    if "predictor" in test_modes:
        mode_runners["predictor"] = lambda: (
            test_inferencing_via_port_forward_to_predictor_svc(
                kube_config,
                model_name,
                inference_path=inference_path,
                payload=payload,
                extract_result=extract_result,
                namespace=namespace,
                port=port,
            )
        )

    if "inferencing-agent" in test_modes:
        mode_runners["inferencing-agent"] = lambda: (
            test_inferencing_via_inferencing_agent(
                kube_config,
                model_name,
                ca_cert,
                cgs_client,
                inferencing_agent_endpoint,
                inference_path=inference_path,
                payload=payload,
            )
        )

    if "ohttp" in test_modes:
        mode_runners["ohttp"] = lambda: test_inferencing_via_ohttp_gateway(
            kube_config,
            model_name,
            inference_path=inference_path,
            payload=payload,
            extract_result=extract_result,
            ohttp_client_image=ohttp_client_image,
            ca_cert=ca_cert,
            cgs_client=cgs_client,
            inferencing_agent_endpoint=inferencing_agent_endpoint,
        )

    if "agent-framework" in test_modes:
        mode_runners["agent-framework"] = lambda: test_inferencing_via_agent_framework(
            kube_config,
            model_name,
            ca_cert=ca_cert,
            cgs_client=cgs_client,
            inferencing_agent_endpoint=inferencing_agent_endpoint,
            namespace=namespace,
        )

    for mode, runner in mode_runners.items():
        try:
            runner()
            test_results[(model_name, mode)] = (True, "")
        except Exception as e:
            print(f"{Colors.RED}[FAIL] {model_name} / {mode}: {e}{Colors.RESET}")
            traceback.print_exc()
            test_results[(model_name, mode)] = (False, str(e))


def test_inferencing_via_agent_framework(
    kube_config: str,
    model_name: str,
    ca_cert: str,
    cgs_client: str,
    inferencing_agent_endpoint: str,
    namespace: str = "kserve-inferencing",
    port: int = 9191,
):
    """Test inference using the Microsoft Agent Framework.

    Launches a temporary Python pod inside the cluster, installs the openai
    and agent-framework packages, and runs an OpenAIChatCompletionClient test
    against the inferencing agent endpoint.  Running from inside the cluster
    avoids DNS and connectivity issues — the pod can resolve both *.svc
    (virtual/onebox) and *.cloudapp.azure.com (AKS) addresses.
    """
    print(
        f"\n{Colors.CYAN}Running AI Agent Framework test for model "
        f"'{model_name}'...{Colors.RESET}"
    )

    kc = f"--kubeconfig {kube_config}"
    configmap_name = "cleanroom-ca-cert"
    test_pod_namespace = "default"
    pod_name = f"agent-fw-test-{model_name}"

    token = get_access_token(cgs_client)

    # Create/update a ConfigMap with the CA certificate.
    try:
        dry_run = subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "create",
                "configmap",
                configmap_name,
                f"--from-file=cleanroomca.crt={ca_cert}",
                "-n",
                test_pod_namespace,
                "--dry-run=client",
                "-o",
                "yaml",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "apply",
                "-f",
                "-",
            ],
            input=dry_run.stdout,
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"Failed to create CA cert ConfigMap: {e}")

    # Clean up any leftover pod from a previous run.
    try:
        run_command(
            f"kubectl {kc} delete pod {pod_name} -n {test_pod_namespace} "
            f"--ignore-not-found"
        )
    except Exception:
        pass

    print(
        f"{Colors.YELLOW}Launching agent-framework test pod "
        f"targeting {inferencing_agent_endpoint}/ai/v1...{Colors.RESET}"
    )

    # Create a Python pod with the CA cert mounted.
    overrides = json.dumps(
        {
            "spec": {
                "containers": [
                    {
                        "name": pod_name,
                        "image": "python:3.12-slim",
                        "command": ["sleep", "3600"],
                        "volumeMounts": [
                            {
                                "name": "ca-cert",
                                "mountPath": "/certs",
                                "readOnly": True,
                            }
                        ],
                    }
                ],
                "volumes": [
                    {
                        "name": "ca-cert",
                        "configMap": {"name": configmap_name},
                    }
                ],
                "restartPolicy": "Never",
            }
        }
    )

    try:
        subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "run",
                pod_name,
                "-n",
                test_pod_namespace,
                "--image=python:3.12-slim",
                f"--overrides={overrides}",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"Failed to launch agent-framework test pod: {e}")

    # Wait for the pod to be ready.
    try:
        run_command(
            f"kubectl {kc} wait --for=condition=Ready pod/{pod_name} "
            f"-n {test_pod_namespace} --timeout=120s"
        )
    except Exception as e:
        raise RuntimeError(f"Agent-framework test pod did not become ready: {e}")

    def _exec_in_pod(cmd: str) -> subprocess.CompletedProcess:
        """Run a command inside the test pod and return the result."""
        return subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "exec",
                pod_name,
                "-n",
                test_pod_namespace,
                "--",
                "sh",
                "-c",
                cmd,
            ],
            capture_output=True,
            text=True,
        )

    # Install required packages inside the pod.
    print(
        f"{Colors.CYAN}Installing openai and agent-framework "
        f"packages in test pod...{Colors.RESET}"
    )

    try:
        install_result = _exec_in_pod(
            "pip install --quiet --pre openai httpx agent-framework-core agent-framework-openai"
        )
        if install_result.returncode != 0:
            print(
                f"{Colors.RED}pip install stderr:\n"
                f"{install_result.stderr}{Colors.RESET}"
            )
            raise RuntimeError(
                f"Failed to install packages in test pod "
                f"(exit {install_result.returncode})."
            )

        # Build the Python test script to run inside the pod.
        base_url = f"{inferencing_agent_endpoint}/ai/v1"
        test_script = f"""
import asyncio
import json
from agent_framework import Agent
from agent_framework.openai import OpenAIChatClient
import httpx
import openai

async def main():
    http_client = httpx.AsyncClient(verify="/certs/cleanroomca.crt")
    async_openai_client = openai.AsyncOpenAI(
        api_key="unused",
        base_url="{base_url}",
        default_headers={{
            "x-ms-cleanroom-authorization": "Bearer {token}",
        }},
        http_client=http_client,
    )
    client = OpenAIChatClient(
        async_client=async_openai_client,
        model="{model_name}",
    )
    agent = Agent(
        client=client,
        name="CleanroomTestAgent",
        instructions="You are a helpful assistant.",
    )
    result = await agent.run("What is the capital of France?")
    print(json.dumps({{"status": "ok", "response": str(result)}}))

asyncio.run(main())
"""

        # Run the test script inside the pod.
        print(f"{Colors.CYAN}Running agent-framework test in pod...{Colors.RESET}")

        # Write the script to a file in the pod to avoid shell escaping issues
        # with multiline Python code.
        write_result = subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "exec",
                pod_name,
                "-n",
                test_pod_namespace,
                "-i",
                "--",
                "sh",
                "-c",
                "cat > /tmp/test_agent.py",
            ],
            input=test_script,
            capture_output=True,
            text=True,
        )
        if write_result.returncode != 0:
            raise RuntimeError(
                f"Failed to write test script to pod: {write_result.stderr}"
            )

        test_result = _exec_in_pod("python3 /tmp/test_agent.py")

        if test_result.returncode != 0:
            print(f"{Colors.RED}Test stderr:\n{test_result.stderr}{Colors.RESET}")
            print(f"{Colors.RED}Test stdout:\n{test_result.stdout}{Colors.RESET}")
            raise RuntimeError(
                f"Agent-framework test failed (exit {test_result.returncode})."
            )

        # Parse and display the result.
        try:
            result_json = json.loads(test_result.stdout.strip())
            print(
                f"{Colors.GREEN}Agent response: {result_json['response']}{Colors.RESET}"
            )
        except (json.JSONDecodeError, KeyError):
            print(
                f"{Colors.GREEN}Agent response: "
                f"{test_result.stdout.strip()}{Colors.RESET}"
            )

        print(
            f"{Colors.GREEN}AI Agent Framework test for '{model_name}' "
            f"passed!{Colors.RESET}"
        )
    finally:
        # Always clean up the test pod.
        try:
            print(f"\n{Colors.YELLOW}Cleaning up pod {pod_name}...{Colors.RESET}")
            run_command(
                f"kubectl {kc} delete pod {pod_name} "
                f"-n {test_pod_namespace} --ignore-not-found"
            )
        except Exception:
            pass


def test_inferencing_via_inferencing_agent(
    kube_config: str,
    model_name: str,
    ca_cert: str,
    cgs_client: str,
    inferencing_agent_endpoint: str,
    inference_path: str,
    payload: dict,
):
    """
    Test a deployed model from inside the cluster using a temporary curl pod.

    Creates a ConfigMap with the CA certificate, launches a temporary pod that
    curls the inferencing agent HTTPS service using its inference via agent FQDN, and verifies
    the inference response.
    """
    print(
        f"\n{Colors.CYAN}Running inference via inference agent endpoint for model "
        f"'{model_name}'...{Colors.RESET}"
    )

    kc = f"--kubeconfig {kube_config}"
    svc_fqdn = inferencing_agent_endpoint
    configmap_name = "cleanroom-ca-cert"

    token = get_access_token(cgs_client)
    test_payload = json.dumps(payload)
    test_pod_namespace = "default"

    # Create/update a ConfigMap with the CA certificate.
    try:
        dry_run = subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "create",
                "configmap",
                configmap_name,
                f"--from-file=cleanroomca.crt={ca_cert}",
                "-n",
                test_pod_namespace,
                "--dry-run=client",
                "-o",
                "yaml",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "apply",
                "-f",
                "-",
            ],
            input=dry_run.stdout,
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"Failed to create CA cert ConfigMap: {e}")

    pod_name = f"curl-test-{model_name}"

    # Clean up any leftover pod from a previous run.
    try:
        run_command(
            f"kubectl {kc} delete pod {pod_name} -n {test_pod_namespace} "
            f"--ignore-not-found"
        )
    except Exception:
        pass

    print(
        f"{Colors.YELLOW}Launching temporary pod to curl "
        f"{svc_fqdn}/ai{inference_path}...{Colors.RESET}"
    )

    # Create a long-lived pod with the CA cert mounted, then exec the curl command.
    try:
        overrides = json.dumps(
            {
                "spec": {
                    "containers": [
                        {
                            "name": pod_name,
                            "image": "curlimages/curl:latest",
                            "command": ["sleep", "3600"],
                            "volumeMounts": [
                                {
                                    "name": "ca-cert",
                                    "mountPath": "/certs",
                                    "readOnly": True,
                                }
                            ],
                        }
                    ],
                    "volumes": [
                        {
                            "name": "ca-cert",
                            "configMap": {"name": configmap_name},
                        }
                    ],
                    "restartPolicy": "Never",
                }
            }
        )
        subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "run",
                pod_name,
                "-n",
                test_pod_namespace,
                "--image=curlimages/curl:latest",
                f"--overrides={overrides}",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"Failed to launch curl test pod: {e}")

    # Wait for the pod to be ready.
    try:
        run_command(
            f"kubectl {kc} wait --for=condition=Ready pod/{pod_name} "
            f"-n {test_pod_namespace} --timeout=60s"
        )
    except Exception as e:
        raise RuntimeError(f"Curl test pod did not become ready: {e}")

    def _exec_curl_in_pod(curl_cmd: str) -> subprocess.CompletedProcess:
        """Run a curl command inside the test pod and return the result."""
        return subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "exec",
                pod_name,
                "-n",
                test_pod_namespace,
                "--",
                "sh",
                "-c",
                curl_cmd,
            ],
            capture_output=True,
            text=True,
        )

    base_url = f"{svc_fqdn}/ai{inference_path}"

    # --- Test 1: Request WITHOUT authorization header (expect 401). ---
    print(
        f"\n{Colors.CYAN}Verifying that request without authorization "
        f"header is rejected with 401...{Colors.RESET}"
    )
    curl_no_auth = (
        f"curl -v --fail-with-body --cacert /certs/cleanroomca.crt "
        f"--retry 10 --retry-delay 5 --retry-connrefused "
        f"-X POST {base_url} "
        f"-H 'Content-Type: application/json' "
        f"-d '{test_payload}'"
    )
    no_auth_result = _exec_curl_in_pod(curl_no_auth)
    if no_auth_result.returncode == 0:
        raise RuntimeError(
            "Expected request without authorization header to fail with 401, "
            "but it succeeded."
        )

    expected_stderr = "The requested URL returned error: 401"
    expected_body = (
        '{"error":{"code":"Unauthorized",'
        '"message":"Expecting Authorization header to be present."}}'
    )
    if expected_stderr not in no_auth_result.stderr:
        print(f"{Colors.RED}Curl stderr:\n{no_auth_result.stderr}{Colors.RESET}")
        raise RuntimeError(
            f"Expected stderr to contain '{expected_stderr}' but it did not."
        )
    if expected_body not in no_auth_result.stdout:
        print(f"{Colors.RED}Curl stdout:\n{no_auth_result.stdout}{Colors.RESET}")
        raise RuntimeError(
            "Expected stdout to contain the ODataError body but it did not."
        )
    print(
        f"{Colors.GREEN}  \u2713 Request without auth header correctly "
        f"rejected with 401 and ODataError body.{Colors.RESET}"
    )

    # --- Test 2: Request WITH authorization header (expect success). ---
    print(
        f"\n{Colors.CYAN}Sending authorized inference request to "
        f"{base_url}...{Colors.RESET}"
    )
    curl_with_auth = (
        f"curl -v --fail-with-body --cacert /certs/cleanroomca.crt "
        f"--retry 10 --retry-delay 5 --retry-connrefused "
        f"-X POST {base_url} "
        f"-H 'Content-Type: application/json' "
        f"-H 'x-ms-cleanroom-authorization: Bearer {token}' "
        f"-d '{test_payload}'"
    )
    auth_result = _exec_curl_in_pod(curl_with_auth)
    if auth_result.returncode != 0:
        print(f"{Colors.RED}Curl stderr:\n{auth_result.stderr}{Colors.RESET}")
        print(f"{Colors.RED}Curl stdout:\n{auth_result.stdout}{Colors.RESET}\n")
        raise RuntimeError(
            f"inference via inference agent endpoint curl test failed (exit {auth_result.returncode})."
        )

    # Print the inference response.
    try:
        result_json = json.loads(auth_result.stdout.strip())
        print(
            f"{Colors.GREEN}inference via inference agent endpoint response:{Colors.RESET}\n"
            f"{json.dumps(result_json, indent=2)}"
        )
    except json.JSONDecodeError:
        print(
            f"{Colors.GREEN}inference via inference agent endpoint response:{Colors.RESET}\n"
            f"{auth_result.stdout}"
        )

    print(
        f"{Colors.GREEN}inference via inference agent endpoint test completed "
        f"successfully!{Colors.RESET}"
    )

    # Verify that connecting via IP fails with a TLS SAN mismatch error.
    # The cert only has the service FQDN in its SAN, not the IP.
    print(
        f"\n{Colors.YELLOW}Verifying that curl via IP fails with "
        f"TLS SAN mismatch...{Colors.RESET}"
    )
    # Resolve the service ClusterIP.
    agent_svc = "kserve-inferencing-agent"
    agent_namespace = "kserve-inferencing-agent"
    try:
        svc_ip_result = subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "get",
                f"svc/{agent_svc}",
                "-n",
                agent_namespace,
                "-o",
                "jsonpath={.spec.clusterIP}",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        svc_ip = svc_ip_result.stdout.strip()
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"Failed to resolve service ClusterIP for {agent_svc}: {e}")

    if svc_ip == "None":
        # Headless service — resolve a pod IP from Endpoints instead.
        try:
            ep_result = subprocess.run(
                [
                    "kubectl",
                    "--kubeconfig",
                    kube_config,
                    "get",
                    f"endpoints/{agent_svc}",
                    "-n",
                    agent_namespace,
                    "-o",
                    "jsonpath={.subsets[0].addresses[0].ip}",
                ],
                capture_output=True,
                text=True,
                check=True,
            )
            svc_ip = ep_result.stdout.strip()
        except subprocess.CalledProcessError as e:
            raise RuntimeError(
                f"Failed to resolve pod IP from endpoints for {agent_svc}: {e}"
            )
        if not svc_ip:
            raise RuntimeError(
                f"No ready endpoints found for headless service {agent_svc}"
            )
        print(
            f"{Colors.YELLOW}  Service '{agent_svc}' is headless, "
            f"using pod IP {svc_ip} for TLS SAN mismatch test.{Colors.RESET}"
        )

    curl_cmd_ip = (
        f"curl -v --fail-with-body --cacert /certs/cleanroomca.crt "
        f"-X POST https://{svc_ip}/ai{inference_path} "
        f"-H 'Content-Type: application/json' "
        f"-d '{test_payload}'"
    )
    ip_result = subprocess.run(
        [
            "kubectl",
            "--kubeconfig",
            kube_config,
            "exec",
            pod_name,
            "-n",
            test_pod_namespace,
            "--",
            "sh",
            "-c",
            curl_cmd_ip,
        ],
        capture_output=True,
        text=True,
    )
    if ip_result.returncode == 0:
        raise RuntimeError(
            "Expected curl via svc IP to fail with TLS SAN mismatch, "
            "but it succeeded unexpectedly."
        )
    if (
        "curl: (60) SSL: no alternative certificate subject name matches target ipv4 address"
        in ip_result.stderr
    ):
        print(
            f"{Colors.GREEN}  ✓ Curl via svc IP ({svc_ip}) failed with expected "
            f"TLS error (exit code {ip_result.returncode}).{Colors.RESET}"
        )
    else:
        print(
            f"{Colors.YELLOW}  Curl via svc IP ({svc_ip}) failed with exit code "
            f"{ip_result.returncode} but error may not be TLS-related:\n"
            f"{ip_result.stderr}{Colors.RESET}"
        )

    # Clean up the test pod.
    try:
        print(f"\n{Colors.YELLOW}Cleaning up pod {pod_name}...{Colors.RESET}")
        run_command(
            f"kubectl {kc} delete pod {pod_name} -n {test_pod_namespace} --ignore-not-found"
        )
    except Exception:
        pass


def _wait_for_port(port: int, timeout: int = 60):
    start = time.time()
    while time.time() - start < timeout:
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
                s.settimeout(1)
                if s.connect_ex(("localhost", port)) == 0:
                    return
        except Exception:
            pass
        time.sleep(0.5)
    raise RuntimeError(f"Port {port} not ready within {timeout}s")


def _test_ohttp_gateway_auth(
    kube_config: str,
    model_name: str,
    cgs_client: str,
    server_endpoint: str,
    pod_name: str,
    pod_namespace: str,
):
    """Validate ext_authz on OHTTP gateway routes by exec-ing curl in an existing pod.

    Tests:
      1. GET /.well-known/ohttp-keys without auth → 200 (public endpoint).
      2. POST /ohttp-gateway/gateway/{model} without auth → 401.
      3. POST /ohttp-gateway/gateway/{model} with auth → not 401 (request
         reaches the ohttp-gateway sidecar which may reject it as non-OHTTP
         content, but the ext_authz check passed).
    """
    print(
        f"\n{Colors.CYAN}Validating OHTTP gateway ext_authz for "
        f"'{model_name}'...{Colors.RESET}"
    )

    def _exec_curl(cmd: str) -> subprocess.CompletedProcess:
        return subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "exec",
                pod_name,
                "-n",
                pod_namespace,
                "--",
                "sh",
                "-c",
                cmd,
            ],
            capture_output=True,
            text=True,
        )

    cacert = "--cacert /certs/cleanroomca.crt"
    retry = "--retry 10 --retry-delay 5 --retry-connrefused"

    # --- Test 1: .well-known/ohttp-keys without auth (public) → 200. ---
    keys_url = f"{server_endpoint}/ohttp-gateway/.well-known/ohttp-keys"
    print(
        f"\n{Colors.CYAN}Verifying .well-known/ohttp-keys is accessible "
        f"without auth...{Colors.RESET}"
    )
    # Response is binary (HPKE key config), so discard body and check HTTP code.
    result = _exec_curl(
        f"curl -v -s -o /dev/null -w '%{{http_code}}' {cacert} {retry} {keys_url}"
    )
    http_code = result.stdout.strip().strip("'")
    if http_code != "200":
        print(f"{Colors.RED}Curl stderr:\n{result.stderr}{Colors.RESET}")
        raise RuntimeError(
            "Expected .well-known/ohttp-keys to return 200 without auth, "
            f"but got HTTP {http_code}."
        )
    print(
        f"{Colors.GREEN}  \u2713 .well-known/ohttp-keys accessible without "
        f"auth header.{Colors.RESET}"
    )

    # --- Test 2: /ohttp-gateway/gateway/{model} without auth → 401. ---
    gateway_url = f"{server_endpoint}/ohttp-gateway/gateway/{model_name}"
    print(
        f"\n{Colors.CYAN}Verifying /ohttp-gateway/gateway/{model_name} "
        f"is rejected without auth...{Colors.RESET}"
    )
    result = _exec_curl(
        f"curl -v --fail-with-body {cacert} {retry} "
        f"-X POST {gateway_url} "
        f"-H 'Content-Type: message/ohttp-req' "
        f"-d 'dummy'"
    )
    if result.returncode == 0:
        raise RuntimeError(
            "Expected /ohttp-gateway/gateway without auth to fail, but it succeeded."
        )
    expected_stderr = "The requested URL returned error: 401"
    if expected_stderr not in result.stderr:
        print(f"{Colors.RED}Curl stderr:\n{result.stderr}{Colors.RESET}")
        raise RuntimeError(
            f"Expected stderr to contain '{expected_stderr}' but it did not."
        )
    print(
        f"{Colors.GREEN}  \u2713 /ohttp-gateway/gateway without auth correctly "
        f"rejected with 401.{Colors.RESET}"
    )

    # --- Test 3: /ohttp-gateway/gateway/{model} with auth → ext_authz passes. ---
    # The request body is not valid OHTTP content so the ohttp-gateway will
    # reject it, but what matters is that Envoy's ext_authz allowed it through
    # (i.e. we get something other than 401).
    token = get_access_token(cgs_client)
    print(
        f"\n{Colors.CYAN}Verifying /ohttp-gateway/gateway/{model_name} "
        f"passes ext_authz with auth header...{Colors.RESET}"
    )
    result = _exec_curl(
        f"curl -v -s -o /dev/null -w '%{{http_code}}' {cacert} {retry} "
        f"-X POST {gateway_url} "
        f"-H 'Content-Type: message/ohttp-req' "
        f"-H 'x-ms-cleanroom-authorization: Bearer {token}' "
        f"-d 'dummy'"
    )
    http_code = result.stdout.strip().strip("'")
    if http_code == "401":
        print(f"{Colors.RED}Curl stderr:\n{result.stderr}{Colors.RESET}")
        raise RuntimeError(
            "Expected /ohttp-gateway/gateway with auth to pass ext_authz, but got 401."
        )
    print(
        f"{Colors.GREEN}  \u2713 /ohttp-gateway/gateway with auth passed "
        f"ext_authz (HTTP {http_code}).{Colors.RESET}"
    )

    print(f"{Colors.GREEN}OHTTP gateway ext_authz validation passed!{Colors.RESET}")


def test_inferencing_via_ohttp_gateway(
    kube_config: str,
    model_name: str,
    inference_path: str,
    payload: dict,
    extract_result,
    ohttp_client_image: str,
    ca_cert: str,
    cgs_client: str,
    inferencing_agent_endpoint: str,
    ohttp_client_namespace: str = "default",
    ohttp_client_port: int = 8070,
    port: int = 8787,
):
    """Test inference through the OHTTP gateway using ohttp-client as a transparent proxy.

    Deploys an ohttp-client pod in the cluster, port-forwards to it, and sends
    a plain HTTP inference request. The ohttp-client encapsulates the request in OHTTP,
    forwards it to the ohttp-gateway sidecar, which decapsulates and proxies to the
    predictor. The response follows the reverse path.

    Args:
        kube_config: Path to kubeconfig file.
        model_name: Name of the deployed InferenceService.
        inference_path: URL path for inference (e.g. /v1/chat/completions).
        payload: JSON payload to send.
        extract_result: Callable(response_json) -> str to extract display text.
        ohttp_client_image: Docker image for the ohttp-client container.
        ca_cert: Path to the cleanroom CA certificate file.
        ohttp_client_namespace: Namespace to deploy the ohttp-client pod.
        ohttp_client_port: Port the ohttp-client listens on inside the pod.
        port: Local port for port-forwarding to the ohttp-client pod.
    """
    print(
        f"\n{Colors.CYAN}Testing OHTTP inference for '{model_name}' "
        f"via ohttp-client...{Colors.RESET}"
    )

    kc = f"--kubeconfig {kube_config}"
    pod_name = f"ohttp-client-test-{model_name}"
    server_endpoint = inferencing_agent_endpoint

    # Clean up any leftover pod.
    try:
        run_command(
            f"kubectl {kc} delete pod {pod_name} -n {ohttp_client_namespace} "
            f"--ignore-not-found"
        )
    except Exception:
        pass

    # Create/update a ConfigMap with the CA certificate in the ohttp-client namespace.
    configmap_name = "ohttp-client-ca-cert"
    try:
        dry_run = subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "create",
                "configmap",
                configmap_name,
                f"--from-file=cleanroomca.crt={ca_cert}",
                "-n",
                ohttp_client_namespace,
                "--dry-run=client",
                "-o",
                "yaml",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "apply",
                "-f",
                "-",
            ],
            input=dry_run.stdout,
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"Failed to create CA cert ConfigMap: {e}")

    print(
        f"{Colors.YELLOW}Launching ohttp-client pod '{pod_name}' pointing to "
        f"{server_endpoint}...{Colors.RESET}"
    )

    # Create the ohttp-client pod.
    ca_mount_path = "/certs"
    overrides = json.dumps(
        {
            "spec": {
                "containers": [
                    {
                        "name": pod_name,
                        "image": ohttp_client_image,
                        "imagePullPolicy": "Always",
                        "env": [
                            {
                                "name": "OHTTP_GATEWAY_ENDPOINT",
                                "value": server_endpoint,
                            },
                            {
                                "name": "ASPNETCORE_URLS",
                                "value": f"http://+:{ohttp_client_port}",
                            },
                            {
                                "name": "OHTTP_CA_CERT_PATH",
                                "value": f"{ca_mount_path}/cleanroomca.crt",
                            },
                        ],
                        "ports": [
                            {"containerPort": ohttp_client_port},
                        ],
                        "volumeMounts": [
                            {
                                "name": "ca-cert",
                                "mountPath": ca_mount_path,
                                "readOnly": True,
                            }
                        ],
                    }
                ],
                "volumes": [
                    {
                        "name": "ca-cert",
                        "configMap": {"name": configmap_name},
                    }
                ],
                "restartPolicy": "Never",
            }
        }
    )

    try:
        subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "run",
                pod_name,
                "-n",
                ohttp_client_namespace,
                f"--image={ohttp_client_image}",
                f"--overrides={overrides}",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError as e:
        raise RuntimeError(f"Failed to launch ohttp-client pod: {e}")

    # Wait for pod to be ready.
    try:
        run_command(
            f"kubectl {kc} wait --for=condition=Ready pod/{pod_name} "
            f"-n {ohttp_client_namespace} --timeout=120s"
        )
    except Exception as e:
        raise RuntimeError(f"ohttp-client pod did not become ready: {e}")

    # --- Auth validation tests using the ohttp-client pod ---
    _test_ohttp_gateway_auth(
        kube_config,
        model_name,
        cgs_client,
        server_endpoint,
        pod_name,
        ohttp_client_namespace,
    )

    # Port-forward to the ohttp-client pod.
    port_forward_process = subprocess.Popen(
        [
            "kubectl",
            "--kubeconfig",
            kube_config,
            "port-forward",
            f"pod/{pod_name}",
            f"--namespace={ohttp_client_namespace}",
            f"{port}:{ohttp_client_port}",
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )

    try:
        _wait_for_port(port)
        wait_for_endpoint_ready(f"http://localhost:{port}")

        inference_url = f"http://localhost:{port}/{model_name}{inference_path}"
        print(
            f"{Colors.CYAN}Sending plain HTTP request to ohttp-client at "
            f"{inference_url}...{Colors.RESET}"
        )

        token = get_access_token(cgs_client)
        request_headers = {
            "Content-Type": "application/json",
            HEADER_AUTHZ: f"Bearer {token}",
        }

        max_retries = 10
        retry_delay = 5
        for attempt in range(1, max_retries + 1):
            response = requests.post(
                inference_url,
                json=payload,
                headers=request_headers,
                timeout=60,
            )
            if response.status_code != 503:
                try:
                    response.raise_for_status()
                except requests.exceptions.HTTPError:
                    print(f"{Colors.RED}Response body: {response.text}{Colors.RESET}")
                    raise
                break
            if attempt < max_retries:
                print(
                    f"{Colors.YELLOW}Attempt {attempt}/{max_retries} failed "
                    f"({response.status_code}). Retrying in {retry_delay}s..."
                    f"{Colors.RESET}"
                )
                time.sleep(retry_delay)
            else:
                try:
                    response.raise_for_status()
                except requests.exceptions.HTTPError:
                    print(f"{Colors.RED}Response body: {response.text}{Colors.RESET}")
                    raise

        result_text = extract_result(response.json())
        print(f"{Colors.GREEN}OHTTP inference response: {result_text}{Colors.RESET}")
        print(
            f"{Colors.GREEN}OHTTP inference test for '{model_name}' "
            f"passed!{Colors.RESET}"
        )

        # For chat/completions models, verify that the response actually streams
        # incrementally. Non-chat models (e.g. sklearn v2) produce small responses
        # that don't meaningfully stream, so skip for those.
        if "/v1/chat/completions" in inference_path:
            print(
                f"\n{Colors.CYAN}Verifying streaming delivery for "
                f"'{model_name}'...{Colors.RESET}"
            )
            streaming_payload = {
                "messages": [
                    {
                        "role": "user",
                        "content": (
                            "Write me a 1 verse song about goldfish on the moon"
                        ),
                    }
                ],
                "max_tokens": 300,
            }
            stream_response = requests.post(
                inference_url,
                json=streaming_payload,
                headers=request_headers,
                timeout=120,
                stream=True,
            )
            stream_response.raise_for_status()

            # Verify the response uses chunked transfer encoding, which proves
            # the ohttp-client is streaming incrementally. TCP coalescing through
            # kubectl port-forward may deliver all data in one read, so we check
            # the transfer encoding header rather than counting iter_content yields.
            transfer_encoding = stream_response.headers.get("Transfer-Encoding", "")
            print(f"  Transfer-Encoding: {transfer_encoding or '(not set)'}")
            if "chunked" in transfer_encoding.lower():
                print(
                    f"{Colors.GREEN}  \u2713 Response uses chunked transfer "
                    f"encoding \u2014 streaming confirmed!{Colors.RESET}"
                )
            else:
                print(
                    f"{Colors.YELLOW}  \u26a0 Response does not use chunked "
                    f"transfer encoding.{Colors.RESET}"
                )

            chunk_count = 0
            total_size = 0
            first_chunk_time = None
            collected_chunks = []
            for chunk in stream_response.iter_content(chunk_size=1024):
                if chunk:
                    chunk_count += 1
                    total_size += len(chunk)
                    collected_chunks.append(chunk)
                    print(
                        f"  chunk #{chunk_count} ({len(chunk)} bytes): "
                        f"{chunk.decode('utf-8', errors='replace')[:200]}"
                    )
                    if first_chunk_time is None:
                        first_chunk_time = time.time()

            stream_body = b"".join(collected_chunks)
            print(
                f"{Colors.GREEN}  Streamed response body:\n"
                f"{stream_body.decode('utf-8', errors='replace')}{Colors.RESET}"
            )
            # Verify JSON validity of streamed response.
            json.loads(stream_body)

            print(
                f"{Colors.GREEN}  Streaming verification: received {chunk_count} "
                f"chunk(s), {total_size} bytes total.{Colors.RESET}"
            )
    finally:
        port_forward_process.terminate()
        try:
            port_forward_process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            port_forward_process.kill()
            port_forward_process.wait()

        # Clean up the ohttp-client test pod.
        try:
            run_command(
                f"kubectl {kc} delete pod {pod_name} -n {ohttp_client_namespace} "
                f"--ignore-not-found"
            )
        except Exception:
            pass


def _print_test_summary(
    test_results: TestResults, *, expect_no_tests: bool = False
) -> bool:
    """Print a model x mode matrix table and return True if all tests passed."""
    if not test_results:
        if expect_no_tests:
            print(
                f"\n{Colors.YELLOW}No tests were executed (submit-only mode).{Colors.RESET}"
            )
            return True
        print(f"\n{Colors.RED}No tests were executed.{Colors.RESET}")
        return False

    # Derive the ordered list of models and modes from the results.
    models: list[str] = []
    modes: list[str] = []
    seen_models: set[str] = set()
    seen_modes: set[str] = set()
    for model, mode in test_results:
        if model not in seen_models:
            models.append(model)
            seen_models.add(model)
        if mode not in seen_modes:
            modes.append(mode)
            seen_modes.add(mode)

    # Column widths.
    model_col_w = max(len(m) for m in models) + 2
    mode_col_w = max(max(len(m) for m in modes) + 2, 8)

    header = "Model".ljust(model_col_w) + "".join(m.ljust(mode_col_w) for m in modes)
    separator = "-" * len(header)

    print(f"\n{Colors.CYAN}{separator}{Colors.RESET}")
    print(f"{Colors.CYAN}Test Summary{Colors.RESET}")
    print(f"{Colors.CYAN}{separator}{Colors.RESET}")
    print(f"{Colors.CYAN}{header}{Colors.RESET}")
    print(f"{Colors.CYAN}{separator}{Colors.RESET}")

    total = 0
    passed = 0
    failed_entries: list[tuple[str, str, str]] = []
    for model in models:
        row = model.ljust(model_col_w)
        for mode in modes:
            key = (model, mode)
            if key not in test_results:
                # Not all models run all modes (e.g. iris skips agent-framework).
                row += "--".ljust(mode_col_w)
            else:
                total += 1
                ok, err = test_results[key]
                if ok:
                    passed += 1
                    row += f"{Colors.GREEN}PASS{Colors.RESET}".ljust(
                        mode_col_w + len(Colors.GREEN) + len(Colors.RESET)
                    )
                else:
                    failed_entries.append((model, mode, err))
                    row += f"{Colors.RED}FAIL{Colors.RESET}".ljust(
                        mode_col_w + len(Colors.RED) + len(Colors.RESET)
                    )
        print(row)

    print(f"{Colors.CYAN}{separator}{Colors.RESET}")
    print(
        f"{Colors.CYAN}Total: {total}  "
        f"{Colors.GREEN}Passed: {passed}  "
        f"{Colors.RED}Failed: {total - passed}{Colors.RESET}"
    )

    if failed_entries:
        print(f"\n{Colors.RED}Failed tests:{Colors.RESET}")
        for model, mode, err in failed_entries:
            print(f"  {Colors.RED}{model} / {mode}: {err}{Colors.RESET}")

    print(f"{Colors.CYAN}{separator}{Colors.RESET}")
    return len(failed_entries) == 0


def _spread_replicas_affinity(model_name: str) -> dict:
    """Return a pod anti-affinity dict that spreads replicas of the same
    model across different nodes."""
    return {
        "podAntiAffinity": {
            "preferredDuringSchedulingIgnoredDuringExecution": [
                {
                    "weight": 100,
                    "podAffinityTerm": {
                        "labelSelector": {
                            "matchExpressions": [
                                {
                                    "key": "serving.kserve.io/inferenceservice",
                                    "operator": "In",
                                    "values": [model_name],
                                }
                            ]
                        },
                        "topologyKey": "kubernetes.io/hostname",
                    },
                }
            ]
        }
    }


def main():
    parser = argparse.ArgumentParser(
        description="Deploy example models to KServe inferencing service"
    )
    parser.add_argument(
        "--deployment-config-dir",
        default=None,
        help="Directory containing deployment configuration files (defaults to script_dir/../../workloads/generated)",
    )
    parser.add_argument(
        "--out-dir",
        default=None,
        help="Output directory (default: script_dir/generated)",
    )
    parser.add_argument(
        "--host-network",
        type=str,
        choices=["true", "false"],
        default=None,
        help="Enable or disable host networking for the inference service pod.",
    )
    parser.add_argument(
        "--models",
        type=str,
        default="default",
        help="Comma-separated list of models to deploy: iris,tinyllama-gpu,gemma4-gpu,tinyllama,phi4-gpu,default "
        "(default: default, note: phi4-gpu, tinyllama-gpu and gemma4-gpu require GPU and are not included in 'default')",
    )
    parser.add_argument(
        "--mode",
        type=str,
        default="default",
        help="Comma-separated list of test modes: predictor,inferencing-agent,ohttp,agent-framework,default (default: default)",
    )
    parser.add_argument(
        "--no-delete",
        action="store_true",
        default=False,
        help="Skip deleting any existing InferenceService before deploying.",
    )
    parser.add_argument(
        "--use-existing-deployment",
        action="store_true",
        default=False,
        help="Skip model deployment and reuse existing InferenceServices. "
        "Waits for the deployment to be ready then runs tests only.",
    )
    parser.add_argument(
        "--submit-only",
        action="store_true",
        default=False,
        help="Submit the model CRD and verify acceptance, then clean up. "
        "Skips wait-for-deployment and inference testing. "
        "Useful for validating GPU spec on CPU-only clusters.",
    )

    args = parser.parse_args()

    script_dir = Path(__file__).parent
    out_dir = args.out_dir if args.out_dir else str(script_dir / "generated")
    deployment_config_dir = args.deployment_config_dir or str(
        script_dir / ".." / ".." / "workloads" / "generated"
    )

    # Parse test modes.
    test_modes = set(m.strip() for m in args.mode.split(","))
    default_mode = "default" in test_modes
    if default_mode:
        test_modes = {"predictor", "inferencing-agent", "ohttp"}

    # Parse models to deploy.
    enabled_models = set(m.strip() for m in args.models.split(","))
    run_default = "default" in enabled_models
    run_iris = run_default or "iris" in enabled_models
    run_tinyllama = run_default or "tinyllama" in enabled_models
    run_tinyllama_gpu = (
        "tinyllama-gpu" in enabled_models
    )  # Not included in "default" (requires GPU).
    run_gemma4_gpu = (
        "gemma4-gpu" in enabled_models
    )  # Not included in "default" (requires GPU).
    run_phi4_gpu = (
        "phi4-gpu" in enabled_models
    )  # Not included in "default" (requires GPU).

    # Load job configuration from out_dir
    config_path = Path(out_dir) / "deployModelConfig.json"
    with open(config_path, "r") as f:
        job_config = json.load(f)

    cgs_client = job_config["cgsClient"]
    inferencing_agent_endpoint = job_config["inferencingAgentEndpoint"]

    # Load deployment config to derive image references.
    deploy_config_path = Path(deployment_config_dir) / "deployment-config.json"
    with open(deploy_config_path, "r") as f:
        deploy_config = json.load(f)
    image_repo = deploy_config["repo"]
    image_tag = deploy_config["tag"]
    ohttp_client_image = f"{image_repo}/workloads/ohttp-client:{image_tag}"

    # Get kube_config from deployment config directory
    kube_config = f"{deployment_config_dir}/cl-cluster/k8s-credentials.yaml"

    # Start kubectl proxy on port 8282
    start_kubectl_proxy(kube_config)

    # Fixed inferencing endpoint using kubectl proxy
    inferencing_endpoint = (
        f"http://localhost:{KUBECTL_PROXY_PORT}/api/v1/namespaces/kserve-inferencing-agent/services/"
        "https:kserve-inferencing-agent:443/proxy"
    )
    print(f"Using inferencing endpoint: {inferencing_endpoint}")

    # Track per (model, mode) pass/fail for the summary matrix.
    test_results: TestResults = {}

    try:
        # Wait for endpoint to be ready
        wait_for_endpoint_ready(inferencing_endpoint, timeout_minutes=1)

        token = get_access_token(cgs_client)
        ca_cert = str(Path(out_dir) / "cleanroomca.crt")

        def deploy_model(
            model_name: str,
            config_file: str,
            display_name: str,
            body_builder,
            port: int = 8989,
            inference_path: str = "/v1/chat/completions",
            payload: dict = None,
            extract_result=None,
            extra_test_modes: set = None,
            exclude_test_modes: set = None,
            post_deploy_hook=None,
        ):
            """Generic deploy-and-test for any model-runtime combination."""
            nonlocal token
            correlation_id = str(uuid.uuid4())
            name = model_name

            if not args.use_existing_deployment:
                config_path_m = Path(out_dir) / config_file
                if not config_path_m.exists():
                    print(
                        f"{Colors.YELLOW}{config_file} not found, "
                        f"skipping {display_name}.{Colors.RESET}"
                    )
                    return

                print(f"\n{Colors.CYAN}{'=' * 60}{Colors.RESET}")
                print(f"{Colors.CYAN}Deploying {display_name}...{Colors.RESET}")

                with open(config_path_m, "r") as f:
                    model_id = json.load(f)["modelDocumentId"]

                body = body_builder(name, model_id)
                if args.host_network is not None:
                    body["placement"] = {"hostNetwork": args.host_network == "true"}

                if not args.no_delete:
                    cleanup_old_model_deployment(kube_config, name)
                token = get_access_token(cgs_client)
                submit_model_deployment(
                    inferencing_endpoint, token, name, correlation_id, body
                )

            if args.submit_only:
                print(
                    f"{Colors.GREEN}Submit-only: CRD for '{name}' "
                    f"accepted successfully. Cleaning up.{Colors.RESET}"
                )
                cleanup_old_model_deployment(kube_config, name)
                return

            wait_for_deployment(
                inferencing_endpoint,
                cgs_client,
                name,
                correlation_id,
                timeout_minutes=30,
            )
            print(f"{Colors.GREEN}{display_name} deployment completed!{Colors.RESET}")

            if post_deploy_hook:
                post_deploy_hook(name)

            modes = set(test_modes)
            if extra_test_modes:
                modes |= extra_test_modes
            if exclude_test_modes:
                modes -= exclude_test_modes

            test_inference(
                kube_config,
                name,
                port=port,
                inference_path=inference_path,
                payload=payload,
                extract_result=extract_result,
                ca_cert=ca_cert,
                cgs_client=cgs_client,
                inferencing_agent_endpoint=inferencing_agent_endpoint,
                ohttp_client_image=ohttp_client_image,
                test_results=test_results,
                test_modes=modes,
            )

        # --- Chat completion helpers (shared payload/extract for LLM models) ---
        def chat_payload(model_name):
            return {
                "model": model_name,
                "messages": [
                    {"role": "system", "content": "You are a helpful assistant."},
                    {
                        "role": "user",
                        "content": (
                            "What is the capital of France? Answer in one sentence."
                        ),
                    },
                ],
                "max_tokens": 100,
            }

        chat_extract = lambda r: r["choices"][0]["message"]["content"][:200]

        # --- Iris + sklearn ---
        if run_iris:

            def iris_post_deploy(name):
                if not args.use_existing_deployment:
                    verify_predictor_deployment_spec(
                        kube_config,
                        name,
                        expected_replicas=2,
                        expected_resources={
                            "requests": {"cpu": "100m", "memory": "256Mi"},
                            "limits": {"cpu": "200m", "memory": "512Mi"},
                        },
                        expected_args=["--workers=1"],
                        expected_env={"SKLEARN_LOG_LEVEL": "INFO"},
                    )

            deploy_model(
                model_name="hello-iris-1",
                config_file="ModelConfig.json",
                display_name="Iris (sklearn)",
                body_builder=lambda name, model_id: {
                    "name": name,
                    "modelId": model_id,
                    "predictor": {
                        "minReplicas": 2,
                        "maxReplicas": 2,
                        "timeout": 60,
                        "affinity": _spread_replicas_affinity(name),
                        "batcher": {
                            "maxBatchSize": 32,
                            "maxLatency": 500,
                            "timeout": 30,
                        },
                        "deploymentStrategy": {
                            "type": "RollingUpdate",
                            "rollingUpdate": {
                                "maxUnavailable": "25%",
                                "maxSurge": "25%",
                            },
                        },
                        "scaleMetricType": "Utilization",
                        "autoScaling": {
                            "metrics": [
                                {
                                    "type": "Resource",
                                    "resource": {
                                        "name": "cpu",
                                        "target": {
                                            "type": "Utilization",
                                            "averageUtilization": 80,
                                        },
                                    },
                                }
                            ],
                        },
                        "model": {
                            "modelFormat": {"name": "sklearn"},
                            "protocolVersion": "v2",
                            "runtime": "kserve-sklearnserver",
                            "resources": {
                                "requests": {"cpu": "100m", "memory": "256Mi"},
                                "limits": {"cpu": "200m", "memory": "512Mi"},
                            },
                            "args": ["--workers=1", "--enable_docs_url=True"],
                            "env": [
                                {"name": "SKLEARN_LOG_LEVEL", "value": "INFO"},
                            ],
                        },
                    },
                },
                inference_path=f"/v2/models/hello-iris-1/infer",
                payload={
                    "inputs": [
                        {
                            "name": "input-0",
                            "shape": [2, 4],
                            "datatype": "FP32",
                            "data": [
                                [6.8, 2.8, 4.8, 1.4],
                                [6.0, 3.4, 4.5, 1.6],
                            ],
                        }
                    ]
                },
                extract_result=lambda r: json.dumps(r["outputs"], indent=2),
                exclude_test_modes={"agent-framework"},
                post_deploy_hook=iris_post_deploy,
            )

        # --- TinyLlama-1.1B-Chat + llama.cpp ---
        if run_tinyllama:

            def tinyllama_post_deploy(name):
                if not args.use_existing_deployment:
                    verify_predictor_deployment_spec(
                        kube_config,
                        name,
                        expected_replicas=2,
                        expected_resources={
                            "requests": {"cpu": "300m", "memory": "1Gi"},
                            "limits": {"cpu": "1", "memory": "2Gi"},
                        },
                        expected_args=["--port"],
                    )

            deploy_model(
                model_name="hello-tinyllama-1",
                config_file="TinyLlamaCpuModelConfig.json",
                display_name="TinyLlama-1.1B-Chat (llama.cpp)",
                body_builder=lambda name, model_id: {
                    "name": name,
                    "modelId": model_id,
                    "predictor": {
                        "minReplicas": 2,
                        "timeout": 180,
                        "affinity": _spread_replicas_affinity(name),
                        "model": {
                            "modelFormat": {"name": "gguf"},
                            "runtime": "llamacpp-server",
                            "resources": {
                                "requests": {"cpu": "300m", "memory": "1Gi"},
                                "limits": {"cpu": "1", "memory": "2Gi"},
                            },
                            "args": ["--port", "8080"],
                        },
                    },
                },
                port=9292,
                payload=chat_payload("hello-tinyllama-1"),
                extract_result=chat_extract,
                extra_test_modes={"agent-framework"} if default_mode else None,
                post_deploy_hook=tinyllama_post_deploy,
            )

        # --- TinyLlama-1.1B-Chat GPU + llama.cpp CUDA ---
        if run_tinyllama_gpu:

            def tinyllama_gpu_post_deploy(name):
                if not args.use_existing_deployment:
                    verify_predictor_deployment_spec(
                        kube_config,
                        name,
                        expected_replicas=2,
                        expected_resources={
                            "requests": {
                                "cpu": "2",
                                "memory": "8Gi",
                                "nvidia.com/gpu": "1",
                            },
                            "limits": {
                                "cpu": "4",
                                "memory": "16Gi",
                                "nvidia.com/gpu": "1",
                            },
                        },
                        expected_args=["--port", "-ngl"],
                    )
                verify_all_replicas_functional(
                    kube_config,
                    name,
                    payload=chat_payload(name),
                    extract_result=chat_extract,
                    expected_count=2,
                )

            deploy_model(
                model_name="hello-tinyllama-gpu-1",
                config_file="TinyLlamaGpuModelConfig.json",
                display_name="TinyLlama-1.1B-Chat GPU (llama.cpp CUDA)",
                body_builder=lambda name, model_id: {
                    "name": name,
                    "modelId": model_id,
                    "predictor": {
                        "minReplicas": 2,
                        "timeout": 120,
                        "model": {
                            "modelFormat": {"name": "gguf"},
                            "runtime": "llamacpp-server-cuda",
                            "resources": {
                                "requests": {
                                    "cpu": "2",
                                    "memory": "8Gi",
                                    "nvidia.com/gpu": "1",
                                },
                                "limits": {
                                    "cpu": "4",
                                    "memory": "16Gi",
                                    "nvidia.com/gpu": "1",
                                },
                            },
                            "args": ["--port", "8080", "-ngl", "99"],
                        },
                    },
                },
                port=9092,
                payload=chat_payload("hello-tinyllama-gpu-1"),
                extract_result=chat_extract,
                extra_test_modes={"agent-framework"} if default_mode else None,
                post_deploy_hook=tinyllama_gpu_post_deploy,
            )

        # --- Gemma 4 31B-IT + vLLM ---
        if run_gemma4_gpu:

            def gemma4_post_deploy(name):
                if not args.use_existing_deployment:
                    verify_predictor_deployment_spec(
                        kube_config,
                        name,
                        expected_replicas=1,
                        expected_resources={
                            "requests": {
                                "cpu": "4",
                                "memory": "64Gi",
                                "nvidia.com/gpu": "1",
                            },
                            "limits": {
                                "cpu": "8",
                                "memory": "128Gi",
                                "nvidia.com/gpu": "1",
                            },
                        },
                        expected_args=[
                            "--max-model-len",
                            "--gpu-memory-utilization",
                        ],
                    )

            deploy_model(
                model_name="hello-gemma4-gpu-1",
                config_file="Gemma4ModelConfig.json",
                display_name="Gemma 4 31B-IT (vLLM)",
                body_builder=lambda name, model_id: {
                    "name": name,
                    "modelId": model_id,
                    "predictor": {
                        "minReplicas": 1,
                        "timeout": 300,
                        "model": {
                            "modelFormat": {"name": "safetensors"},
                            "runtime": "vllm-openai",
                            "resources": {
                                "requests": {
                                    "cpu": "4",
                                    "memory": "64Gi",
                                    "nvidia.com/gpu": "1",
                                },
                                "limits": {
                                    "cpu": "8",
                                    "memory": "128Gi",
                                    "nvidia.com/gpu": "1",
                                },
                            },
                            "args": [
                                "--port",
                                "8080",
                                "--max-model-len",
                                "8192",
                                "--gpu-memory-utilization",
                                "0.90",
                                "--served-model-name",
                                name,
                            ],
                        },
                    },
                },
                port=9094,
                payload=chat_payload("hello-gemma4-gpu-1"),
                extract_result=chat_extract,
                extra_test_modes={"agent-framework"} if default_mode else None,
                post_deploy_hook=gemma4_post_deploy,
            )

        # --- Phi-4 14B + vLLM ---
        if run_phi4_gpu:

            def phi4_post_deploy(name):
                if not args.use_existing_deployment:
                    verify_predictor_deployment_spec(
                        kube_config,
                        name,
                        expected_replicas=1,
                        expected_resources={
                            "requests": {
                                "cpu": "4",
                                "memory": "32Gi",
                                "nvidia.com/gpu": "1",
                            },
                            "limits": {
                                "cpu": "8",
                                "memory": "64Gi",
                                "nvidia.com/gpu": "1",
                            },
                        },
                        expected_args=[
                            "--max-model-len",
                            "--gpu-memory-utilization",
                        ],
                    )

            deploy_model(
                model_name="hello-phi4-gpu-1",
                config_file="Phi4ModelConfig.json",
                display_name="Phi-4 14B (vLLM)",
                body_builder=lambda name, model_id: {
                    "name": name,
                    "modelId": model_id,
                    "predictor": {
                        "minReplicas": 1,
                        "timeout": 300,
                        "model": {
                            "modelFormat": {"name": "safetensors"},
                            "runtime": "vllm-openai",
                            "resources": {
                                "requests": {
                                    "cpu": "4",
                                    "memory": "32Gi",
                                    "nvidia.com/gpu": "1",
                                },
                                "limits": {
                                    "cpu": "8",
                                    "memory": "64Gi",
                                    "nvidia.com/gpu": "1",
                                },
                            },
                            "args": [
                                "--port",
                                "8080",
                                "--max-model-len",
                                "8192",
                                "--gpu-memory-utilization",
                                "0.90",
                                "--served-model-name",
                                name,
                            ],
                        },
                    },
                },
                port=9096,
                payload=chat_payload("hello-phi4-gpu-1"),
                extract_result=chat_extract,
                extra_test_modes={"agent-framework"} if default_mode else None,
                post_deploy_hook=phi4_post_deploy,
            )

    except Exception as e:
        print(f"{Colors.RED}Error: {e}{Colors.RESET}")
        traceback.print_exc()
        # Print whatever results were collected, then exit with failure.
        _print_test_summary(test_results)
        sys.exit(1)

    all_passed = _print_test_summary(test_results, expect_no_tests=args.submit_only)
    if not all_passed:
        sys.exit(1)


if __name__ == "__main__":
    main()
