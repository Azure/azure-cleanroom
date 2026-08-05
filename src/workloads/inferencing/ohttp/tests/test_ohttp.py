#!/usr/bin/env python3
"""Standalone integration tests for the OHTTP client and gateway.

Generates TLS certificates, spins up docker-compose (mock-predictor,
ohttp-gateway, ohttp-client), and runs end-to-end tests through the
OHTTP proxy pipeline.
"""

import argparse
import datetime
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time

import requests
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID


def generate_certs(cert_dir: str):
    """Generate a self-signed CA and a server certificate for mock-predictor."""
    print(f"Generating test certificates in {cert_dir}")

    # CA key and self-signed cert.
    ca_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    ca_name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Test CA")])
    ca_cert = (
        x509.CertificateBuilder()
        .subject_name(ca_name)
        .issuer_name(ca_name)
        .public_key(ca_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(datetime.datetime.now(datetime.timezone.utc))
        .not_valid_after(
            datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=1)
        )
        .add_extension(x509.BasicConstraints(ca=True, path_length=0), critical=True)
        .sign(ca_key, hashes.SHA256())
    )

    # Server key and cert signed by CA.
    server_key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    server_cert = (
        x509.CertificateBuilder()
        .subject_name(
            x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "mock-predictor")])
        )
        .issuer_name(ca_name)
        .public_key(server_key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(datetime.datetime.now(datetime.timezone.utc))
        .not_valid_after(
            datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=1)
        )
        .add_extension(
            x509.SubjectAlternativeName(
                [
                    x509.DNSName("mock-predictor"),
                    x509.DNSName("localhost"),
                ]
            ),
            critical=False,
        )
        .sign(ca_key, hashes.SHA256())
    )

    # Write files.
    with open(os.path.join(cert_dir, "ca.crt"), "wb") as f:
        f.write(ca_cert.public_bytes(serialization.Encoding.PEM))

    with open(os.path.join(cert_dir, "server.crt"), "wb") as f:
        f.write(server_cert.public_bytes(serialization.Encoding.PEM))

    with open(os.path.join(cert_dir, "server.key"), "wb") as f:
        f.write(
            server_key.private_bytes(
                serialization.Encoding.PEM,
                serialization.PrivateFormat.TraditionalOpenSSL,
                serialization.NoEncryption(),
            )
        )


def wait_for_service(url: str, name: str, timeout: int = 60):
    """Poll a URL until it returns 200 or timeout is reached."""
    print(f"Waiting for {name} at {url} ...")
    deadline = time.time() + timeout
    last_error = None
    while time.time() < deadline:
        try:
            r = requests.get(url, timeout=5)
            if r.status_code == 200:
                print(f"  {name} is ready.")
                return
        except requests.ConnectionError as e:
            last_error = e
        time.sleep(1)
    raise TimeoutError(
        f"{name} did not become ready within {timeout}s. Last error: {last_error}"
    )


def run_compose(compose_file: str, cert_dir: str, action: str, *extra_args):
    """Run docker compose with the test environment."""
    env = {**os.environ, "OHTTP_TEST_CERTS_DIR": cert_dir}
    cmd = ["docker", "compose", "-f", compose_file, action, *extra_args]
    print(f"Running: {' '.join(cmd)}")
    subprocess.run(cmd, env=env, check=True)


def compose_logs(compose_file: str, cert_dir: str):
    """Print docker compose logs for debugging."""
    env = {**os.environ, "OHTTP_TEST_CERTS_DIR": cert_dir}
    cmd = ["docker", "compose", "-f", compose_file, "logs", "--tail=50"]
    subprocess.run(cmd, env=env, check=False)


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


def test_health_checks(base_url: str):
    """Verify the /ready endpoint of the ohttp-client."""
    print("\n=== Test: health check ===")
    r = requests.get(f"{base_url}/ready", timeout=10)
    assert r.status_code == 200, f"Expected 200, got {r.status_code}"
    body = r.json()
    assert body.get("status") == "up", f"Unexpected body: {body}"
    print("  PASSED")


def test_v2_infer(base_url: str):
    """Send a KServe v2 inference request through the OHTTP pipeline."""
    print("\n=== Test: v2 infer ===")
    payload = {
        "inputs": [
            {
                "name": "input-0",
                "shape": [2, 2],
                "datatype": "INT64",
                "data": [1, 2, 3, 4],
            }
        ]
    }

    r = requests.post(
        f"{base_url}/test-model/v2/models/test-model/infer",
        json=payload,
        timeout=30,
    )
    assert r.status_code == 200, f"Expected 200, got {r.status_code}: {r.text}"
    body = r.json()
    assert body.get("model_name") == "test-model", f"Unexpected model_name: {body}"
    assert "outputs" in body, f"Missing 'outputs': {body}"
    print(f"  Response: {json.dumps(body, indent=2)}")
    print("  PASSED")


def test_chat_completions(base_url: str):
    """Send a chat completions request through the OHTTP pipeline."""
    print("\n=== Test: chat completions ===")
    payload = {
        "model": "test-model",
        "messages": [{"role": "user", "content": "Hello, world!"}],
    }

    r = requests.post(
        f"{base_url}/test-model/v1/chat/completions",
        json=payload,
        timeout=30,
    )
    assert r.status_code == 200, f"Expected 200, got {r.status_code}: {r.text}"
    body = r.json()
    assert "choices" in body, f"Missing 'choices': {body}"
    content = body["choices"][0]["message"]["content"]
    # The mock returns ~2.4KB of text; verify it came through intact.
    assert len(content) > 1000, f"Response too short ({len(content)} chars)"
    print(f"  Response length: {len(content)} chars")
    print("  PASSED")


def test_chat_completions_streaming(base_url: str):
    """Verify that the response actually streams (chunked transfer-encoding)."""
    print("\n=== Test: chat completions streaming ===")
    payload = {
        "model": "test-model",
        "messages": [{"role": "user", "content": "Stream test"}],
    }

    r = requests.post(
        f"{base_url}/test-model/v1/chat/completions",
        json=payload,
        timeout=30,
        stream=True,
    )
    assert r.status_code == 200, f"Expected 200, got {r.status_code}"

    # Verify the response is actually chunked (no Content-Length).
    transfer_encoding = r.headers.get("transfer-encoding", "")
    content_length = r.headers.get("content-length")
    print(
        f"  Transfer-Encoding: {transfer_encoding!r}, Content-Length: {content_length!r}"
    )
    assert "chunked" in transfer_encoding.lower(), (
        f"Expected chunked transfer-encoding, got {transfer_encoding!r}. "
        f"Content-Length={content_length!r} — response was not streamed."
    )
    assert content_length is None, (
        f"Content-Length should be absent for a streamed response, got {content_length!r}"
    )

    # Read chunks and verify data arrives in multiple pieces.
    chunks = []
    for chunk in r.iter_content(chunk_size=512):
        if chunk:
            chunks.append(chunk)

    total_bytes = sum(len(c) for c in chunks)
    print(f"  Received {len(chunks)} chunks, {total_bytes} bytes total")
    assert total_bytes > 1000, f"Expected >1KB, got {total_bytes}"
    assert len(chunks) > 1, (
        f"Expected multiple chunks for a streamed response, got {len(chunks)}"
    )

    # Verify the full body is valid JSON.
    full_body = b"".join(chunks)
    body = json.loads(full_body)
    assert "choices" in body, f"Missing 'choices' in reassembled body"
    print("  PASSED")


def main():
    parser = argparse.ArgumentParser(description="OHTTP integration tests")
    parser.add_argument(
        "--port",
        type=int,
        default=8070,
        help="Host port mapped to the ohttp-client container.",
    )
    parser.add_argument(
        "--keep",
        action="store_true",
        help="Keep containers running after tests (for debugging).",
    )
    args = parser.parse_args()

    script_dir = os.path.dirname(os.path.abspath(__file__))
    compose_file = os.path.join(script_dir, "docker-compose.yaml")
    cert_dir = tempfile.mkdtemp(prefix="ohttp-test-certs-")

    base_url = f"http://localhost:{args.port}"
    passed = 0
    failed = 0
    errors = []

    try:
        # Step 1: Generate TLS certs.
        generate_certs(cert_dir)

        # Step 2: Start containers.
        run_compose(compose_file, cert_dir, "up", "-d", "--build", "--wait")

        # Step 3: Wait for ohttp-client to be ready.
        wait_for_service(f"{base_url}/ready", "ohttp-client")

        # Step 4: Run tests.
        tests = [
            test_health_checks,
            test_v2_infer,
            test_chat_completions,
            test_chat_completions_streaming,
        ]

        for test_fn in tests:
            try:
                test_fn(base_url)
                passed += 1
            except Exception as e:
                failed += 1
                errors.append((test_fn.__name__, e))
                print(f"  FAILED: {e}")

    except Exception as e:
        print(f"\nFATAL: {e}", file=sys.stderr)
        compose_logs(compose_file, cert_dir)
        raise
    finally:
        if not args.keep:
            print("\nTearing down containers...")
            run_compose(compose_file, cert_dir, "down", "-v")
            shutil.rmtree(cert_dir, ignore_errors=True)
        else:
            print(f"\nContainers kept running. Certs at: {cert_dir}")
            print(f"Tear down manually: docker compose -f {compose_file} down -v")

    # Summary.
    print(f"\n{'=' * 60}")
    print(f"Results: {passed} passed, {failed} failed")
    if errors:
        print("\nFailed tests:")
        for name, err in errors:
            print(f"  - {name}: {err}")
        print(f"{'=' * 60}")
        sys.exit(1)

    print(f"{'=' * 60}")
    print("All tests passed!")


if __name__ == "__main__":
    main()
