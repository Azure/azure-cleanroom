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
import uuid
from datetime import datetime, timedelta
from pathlib import Path

import requests
import urllib3

# Suppress InsecureRequestWarning for verify=False HTTPS calls via port-forward.
urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

# Import shared utilities
_git_root = subprocess.run(
    ["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True
).stdout.strip()
sys.path.insert(0, str(Path(_git_root) / "test" / "onebox"))
from cleanroom_test_utils import Colors, run_command

# Global variable to track kubectl proxy process
_kubectl_proxy_process: subprocess.Popen = None


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


def start_kubectl_proxy(kube_config: str, port: int = 8282) -> None:
    """
    Start kubectl proxy on port 8282 and verify it started successfully.

    Args:
        kube_config: Path to kubeconfig file
        port: Port to use for kubectl proxy (default: 8282)

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

    # Start kubectl proxy
    process = subprocess.Popen(
        ["kubectl", "proxy", "--port", str(port), "--kubeconfig", kube_config],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
    )

    _kubectl_proxy_process = process

    # Wait for proxy to be ready (check if port is listening)
    max_wait = 30  # seconds
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
                result = s.connect_ex(("localhost", port))
                if result == 0:
                    print(
                        f"{Colors.GREEN}kubectl proxy started successfully on port {port}{Colors.RESET}"
                    )
                    return
        except Exception:
            pass

        time.sleep(0.5)

    # Cleanup if we failed to start
    process.terminate()
    process.wait()
    raise RuntimeError(f"kubectl proxy failed to start within {max_wait} seconds")


def get_timestamp():
    """Return formatted timestamp for logging"""
    now = datetime.now()
    return f"[{now.strftime('%m/%d/%y')} {now.strftime('%H:%M:%S')}]"


def wait_for_endpoint_ready(endpoint: str, timeout_minutes: int = 1):
    """
    Wait for the inferencing endpoint to be ready.

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
                print(f"{Colors.GREEN}Inferencing endpoint is ready{Colors.RESET}")
                return
        except Exception:
            pass

        print(f"Waiting for inferencing endpoint to be ready at {endpoint}/ready")
        time.sleep(3)

        if datetime.now() - start_time > timeout:
            raise TimeoutError(
                "Hit timeout waiting for analytics endpoint to be ready."
            )


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
        start_date: Optional start date parameter
        end_date: Optional end date parameter

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
        "x-ms-cleanroom-authorization": f"Bearer {token}",
        "x-ms-correlation-id": correlation_id,
        "x-ms-client-request-id": client_request_id,
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
        "x-ms-cleanroom-authorization": f"Bearer {token}",
        "x-ms-correlation-id": correlation_id,
        "x-ms-client-request-id": client_request_id,
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

        # Check if deployment is complete
        if job_status.get("status", {}).get("url"):
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


def verify_predictor_deployment_spec(
    kube_config: str,
    model_name: str,
    namespace: str = "kserve-inferencing",
    expected_replicas: int = 1,
    expected_resources: dict = None,
    expected_args: list = None,
    expected_env: dict = None,
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
        print(f"{Colors.GREEN}  ✓ Resources match expected " f"spec.{Colors.RESET}")

    # Verify args if expected.
    if expected_args:
        actual_args = serving_container.get("args", [])
        for arg in expected_args:
            if not any(arg in a for a in actual_args):
                raise RuntimeError(
                    f"Expected arg '{arg}' not found in "
                    f"container args: {actual_args}"
                )
        print(f"{Colors.GREEN}  ✓ Args contain expected " f"values.{Colors.RESET}")

    # Verify env vars if expected.
    if expected_env:
        actual_env = {
            e["name"]: e.get("value", "") for e in serving_container.get("env", [])
        }
        for name, value in expected_env.items():
            if actual_env.get(name) != value:
                raise RuntimeError(
                    f"Expected env {name}={value}, " f"got {actual_env.get(name)}"
                )
        print(f"{Colors.GREEN}  ✓ Env vars match expected " f"values.{Colors.RESET}")

    # Verify deployment strategy if present.
    strategy = spec.get("strategy", {})
    if strategy:
        strategy_type = strategy.get("type", "")
        print(
            f"{Colors.GREEN}  ✓ Deployment strategy: " f"{strategy_type}{Colors.RESET}"
        )

    print(
        f"{Colors.GREEN}  Predictor deployment spec verification "
        f"passed.{Colors.RESET}"
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
        f"{Colors.GREEN}  ✓ nodeSelector "
        f"'pod-policy: required' is set.{Colors.RESET}"
    )


def test_inference(
    kube_config: str,
    model_name: str,
    inference_path: str,
    payload: dict,
    extract_result,
    ca_cert: str,
    namespace: str = "kserve-inferencing",
    port: int = 8989,
    timeout_seconds: int = 300,
    inference_timeout: int = 60,
):
    """Test a deployed model by port-forwarding and sending an inference request.

    Args:
        kube_config: Path to kubeconfig file.
        model_name: Name of the deployed InferenceService.
        inference_path: URL path for inference (e.g. /v2/models/{name}/infer).
        payload: JSON payload to send.
        extract_result: Callable(response_json) -> str to extract display text.
    """
    print(f"\n{Colors.CYAN}Testing '{model_name}' in '{namespace}'...{Colors.RESET}")

    kc = f"--kubeconfig {kube_config}"
    run_command(
        f"kubectl {kc} wait --for=condition=Ready "
        f"inferenceservice/{model_name} "
        f"-n {namespace} --timeout={timeout_seconds}s",
    )
    print(f"{Colors.GREEN}InferenceService '{model_name}' is ready!{Colors.RESET}")

    verify_predictor_deployment_placement(kube_config, model_name, namespace)

    # Port-forward directly to the predictor service.
    predictor_svc = f"{model_name}-predictor-https"
    print(
        f"{Colors.CYAN}Starting port-forward to predictor service "
        f"{predictor_svc} in namespace {namespace} on port {port}...{Colors.RESET}"
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
        # Wait for port-forward.
        _wait_for_port(port)

        inference_url = f"https://localhost:{port}{inference_path}"
        max_retries = 10
        retry_delay = 5
        for attempt in range(1, max_retries + 1):
            response = requests.post(
                inference_url,
                json=payload,
                headers={"Content-Type": "application/json"},
                timeout=inference_timeout,
                verify=False,
            )
            if response.status_code != 503:
                response.raise_for_status()
                break
            if attempt < max_retries:
                print(
                    f"{Colors.YELLOW}Attempt {attempt}/{max_retries} failed "
                    f"({response.status_code}). Retrying in {retry_delay}s..."
                    f"{Colors.RESET}"
                )
                time.sleep(retry_delay)
            else:
                response.raise_for_status()

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

    # Run an in-cluster test using a temporary pod that curls the service FQDN directly.
    test_model_deployment_in_cluster(
        kube_config,
        model_name,
        ca_cert,
        inference_path=inference_path,
        payload=payload,
        namespace=namespace,
    )


def test_model_deployment_in_cluster(
    kube_config: str,
    model_name: str,
    ca_cert: str,
    inference_path: str,
    payload: dict,
    namespace: str = "kserve-inferencing",
):
    """
    Test a deployed model from inside the cluster using a temporary curl pod.

    Creates a ConfigMap with the CA certificate, launches a temporary pod that
    curls the predictor HTTPS service using its in-cluster FQDN, and verifies
    the inference response.
    """
    print(
        f"\n{Colors.CYAN}Running in-cluster inference test for model "
        f"'{model_name}'...{Colors.RESET}"
    )

    kc = f"--kubeconfig {kube_config}"
    predictor_svc = f"{model_name}-predictor-https"
    svc_fqdn = f"{predictor_svc}.{namespace}.svc.cluster.local"
    configmap_name = "cleanroom-ca-cert"

    test_payload = json.dumps(payload)

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
                namespace,
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
            f"kubectl {kc} delete pod {pod_name} -n {namespace} "
            f"--ignore-not-found --wait=false"
        )
    except Exception:
        pass

    print(
        f"{Colors.YELLOW}Launching temporary pod to curl "
        f"https://{svc_fqdn}:443{inference_path}...{Colors.RESET}"
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
                namespace,
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
            f"-n {namespace} --timeout=60s"
        )
    except Exception as e:
        raise RuntimeError(f"Curl test pod did not become ready: {e}")

    # Build the curl command to run inside the pod.
    curl_cmd = (
        f"curl -v --fail --cacert /certs/cleanroomca.crt "
        f"--retry 10 --retry-delay 5 --retry-all-errors "
        f"-X POST https://{svc_fqdn}:443{inference_path} "
        f"-H 'Content-Type: application/json' "
        f"-d '{test_payload}'"
    )

    # Exec the curl command inside the running pod.
    try:
        result = subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "exec",
                pod_name,
                "-n",
                namespace,
                "--",
                "sh",
                "-c",
                curl_cmd,
            ],
            capture_output=True,
            text=True,
            check=True,
        )
    except subprocess.CalledProcessError as e:
        print(f"{Colors.RED}Curl stderr:\n{e.stderr}{Colors.RESET}")
        raise RuntimeError(f"In-cluster curl test failed: {e}")

    # Print the inference response.
    try:
        result_json = json.loads(result.stdout.strip())
        print(
            f"{Colors.GREEN}In-cluster inference response:{Colors.RESET}\n"
            f"{json.dumps(result_json, indent=2)}"
        )
    except json.JSONDecodeError:
        print(
            f"{Colors.GREEN}In-cluster inference response:{Colors.RESET}\n"
            f"{result.stdout}"
        )

    print(
        f"{Colors.GREEN}In-cluster model deployment test completed "
        f"successfully!{Colors.RESET}"
    )

    # Verify that connecting via IP fails with a TLS SAN mismatch error.
    # The cert only has the service FQDN in its SAN, not the IP.
    print(
        f"\n{Colors.YELLOW}Verifying that curl via IP fails with "
        f"TLS SAN mismatch...{Colors.RESET}"
    )
    # Resolve the service ClusterIP.
    try:
        svc_ip_result = subprocess.run(
            [
                "kubectl",
                "--kubeconfig",
                kube_config,
                "get",
                f"svc/{predictor_svc}",
                "-n",
                namespace,
                "-o",
                "jsonpath={.spec.clusterIP}",
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        svc_ip = svc_ip_result.stdout.strip()
    except subprocess.CalledProcessError as e:
        raise RuntimeError(
            f"Failed to resolve service ClusterIP for {predictor_svc}: {e}"
        )

    curl_cmd_ip = (
        f"curl -v --fail --cacert /certs/cleanroomca.crt "
        f"-X POST https://{svc_ip}:443{inference_path} "
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
            namespace,
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
            f"kubectl {kc} delete pod {pod_name} -n {namespace} --ignore-not-found"
        )
    except Exception:
        pass


def _wait_for_port(port: int, timeout: int = 20):
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
        default="all",
        help="Comma-separated list of models to deploy: iris,gpt2,all (default: all)",
    )

    args = parser.parse_args()

    script_dir = Path(__file__).parent
    out_dir = args.out_dir if args.out_dir else str(script_dir / "generated")
    deployment_config_dir = args.deployment_config_dir or str(
        script_dir / ".." / ".." / "workloads" / "generated"
    )

    # Parse models to deploy.
    enabled_models = set(m.strip() for m in args.models.split(","))
    run_all = "all" in enabled_models
    run_iris = run_all or "iris" in enabled_models
    run_gpt2 = run_all or "gpt2" in enabled_models

    # Load job configuration from out_dir
    config_path = Path(out_dir) / "deployModelConfig.json"
    with open(config_path, "r") as f:
        job_config = json.load(f)

    cgs_client = job_config["cgsClient"]
    # Get kube_config from deployment config directory
    kube_config = f"{deployment_config_dir}/cl-cluster/k8s-credentials.yaml"

    # Start kubectl proxy on port 8282
    start_kubectl_proxy(kube_config)

    # Fixed inferencing endpoint using kubectl proxy
    inferencing_endpoint = (
        "http://localhost:8282/api/v1/namespaces/kserve-inferencing-agent/services/"
        "https:kserve-inferencing-agent:443/proxy"
    )
    print(f"Using inferencing endpoint: {inferencing_endpoint}")

    try:
        # Wait for endpoint to be ready
        wait_for_endpoint_ready(inferencing_endpoint, timeout_minutes=1)

        token = get_access_token(cgs_client)
        ca_cert = str(Path(out_dir) / "cleanroomca.crt")

        # --- Iris + sklearn ---
        if run_iris:
            correlation_id = str(uuid.uuid4())
            model_name = "hello-iris-1"
            with open(Path(out_dir) / "ModelConfig.json", "r") as f:
                model_config = json.load(f)

            model_id = model_config["modelDocumentId"]
            print(f"Deploying model with ID: {model_id}")

            body = {
                "name": model_name,
                "modelId": model_id,
                "predictor": {
                    "minReplicas": 1,
                    "maxReplicas": 2,
                    "timeout": 60,
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
                            "limits": {"cpu": "500m", "memory": "512Mi"},
                        },
                        "args": ["--workers=1", "--enable_docs_url=True"],
                        "env": [
                            {"name": "SKLEARN_LOG_LEVEL", "value": "INFO"},
                        ],
                    },
                },
            }

            if args.host_network is not None:
                body["placement"] = {"hostNetwork": args.host_network == "true"}

            cleanup_old_model_deployment(kube_config, model_name)

            submit_model_deployment(
                inferencing_endpoint, token, model_name, correlation_id, body
            )

            wait_for_deployment(
                inferencing_endpoint,
                cgs_client,
                model_name,
                correlation_id,
                timeout_minutes=30,
            )

            print(f"{Colors.GREEN}Iris model deployment completed!{Colors.RESET}")

            verify_predictor_deployment_spec(
                kube_config,
                model_name,
                expected_replicas=1,
                expected_resources={
                    "requests": {"cpu": "100m", "memory": "256Mi"},
                    "limits": {"cpu": "500m", "memory": "512Mi"},
                },
                expected_args=["--workers=1"],
                expected_env={"SKLEARN_LOG_LEVEL": "INFO"},
            )

            test_inference(
                kube_config,
                model_name,
                inference_path=f"/v2/models/{model_name}/infer",
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
                ca_cert=ca_cert,
            )

        # --- GPT-2 + llama.cpp ---
        if run_gpt2:
            gpt2_config_path = Path(out_dir) / "Gpt2ModelConfig.json"
            if gpt2_config_path.exists():
                print(f"\n{Colors.CYAN}{'=' * 60}{Colors.RESET}")
                print(f"{Colors.CYAN}Deploying GPT-2 (llama.cpp)...{Colors.RESET}")

                with open(gpt2_config_path, "r") as f:
                    gpt2_model_id = json.load(f)["modelDocumentId"]

                gpt2_name = "hello-gpt2-1"
                gpt2_body = {
                    "name": gpt2_name,
                    "modelId": gpt2_model_id,
                    "predictor": {
                        "minReplicas": 1,
                        "timeout": 120,
                        "model": {
                            "modelFormat": {"name": "gguf"},
                            "runtime": "llamacpp-server",
                            "resources": {
                                "requests": {"cpu": "500m", "memory": "512Mi"},
                                "limits": {"cpu": "1", "memory": "1Gi"},
                            },
                            "args": ["--port", "8080"],
                        },
                    },
                }
                if args.host_network is not None:
                    gpt2_body["placement"] = {
                        "hostNetwork": args.host_network == "true"
                    }

                cleanup_old_model_deployment(kube_config, gpt2_name)
                token = get_access_token(cgs_client)
                submit_model_deployment(
                    inferencing_endpoint,
                    token,
                    gpt2_name,
                    str(uuid.uuid4()),
                    gpt2_body,
                )
                wait_for_deployment(
                    inferencing_endpoint,
                    cgs_client,
                    gpt2_name,
                    str(uuid.uuid4()),
                    timeout_minutes=30,
                )
                print(f"{Colors.GREEN}GPT-2 deployment completed!{Colors.RESET}")

                test_inference(
                    kube_config,
                    gpt2_name,
                    port=9091,
                    inference_path="/v1/chat/completions",
                    payload={
                        "messages": [
                            {"role": "user", "content": "The capital of France is"}
                        ],
                        "max_tokens": 20,
                    },
                    extract_result=lambda r: r["choices"][0]["message"]["content"],
                    ca_cert=ca_cert,
                    inference_timeout=600,
                )
            else:
                print(
                    f"{Colors.YELLOW}Gpt2ModelConfig.json not found, "
                    f"skipping GPT-2.{Colors.RESET}"
                )

    except Exception as e:
        print(f"{Colors.RED}Error: {e}{Colors.RESET}")
        sys.exit(1)


if __name__ == "__main__":
    main()
