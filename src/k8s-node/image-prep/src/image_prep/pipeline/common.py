# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Shared utilities for the image-prep pipeline."""

import logging
import subprocess
import sys
from pathlib import Path

logger = logging.getLogger("image-prep")


def setup_logging() -> None:
    """Configure colored logging for the pipeline."""
    handler = logging.StreamHandler(sys.stderr)
    handler.setFormatter(logging.Formatter("[%(levelname)s] %(message)s"))
    logger.addHandler(handler)
    logger.setLevel(logging.INFO)


def run(
    cmd: list[str],
    *,
    check: bool = True,
    capture: bool = False,
    **kwargs,
) -> subprocess.CompletedProcess:
    """Run a command with logging."""
    logger.debug("Running: %s", " ".join(cmd))
    return subprocess.run(
        cmd,
        check=check,
        capture_output=capture,
        text=True,
        **kwargs,
    )


def az(
    *args: str, capture: bool = False, check: bool = True
) -> subprocess.CompletedProcess:
    """Run an az CLI command."""
    return run(["az", *args], capture=capture, check=check)


def az_json(*args: str) -> dict:
    """Run an az CLI command and return parsed JSON output."""
    result = az(*args, "--output", "json", capture=True)
    import json

    return json.loads(result.stdout)


def az_query(*args: str, query: str) -> str:
    """Run an az CLI command and return a single JMESPath query value."""
    result = az(*args, "--query", query, "--output", "tsv", capture=True)
    return result.stdout.strip()


def azcopy(
    *args: str, env: dict | None = None, check: bool = True
) -> subprocess.CompletedProcess:
    """Run an azcopy command."""
    import os

    merged_env = {**os.environ, **(env or {})}
    return run(["azcopy", *args], env=merged_env, check=check)


# -- Status blob utilities ------------------------------------------------


def _wait_for_rbac(
    account_name: str,
    container_name: str,
    timeout: int = 120,
) -> None:
    """Poll until RBAC allows container operations on the storage account.

    ``--auth-mode login`` operations fail silently (exit 0, no container
    created) when the role assignment hasn't propagated yet. This function
    creates a probe container, verifies it exists, then deletes it.
    """
    import time

    probe = f"{container_name}-probe"
    deadline = time.monotonic() + timeout
    interval = 10

    while True:
        # Attempt to create a probe container.
        create_result = az(
            "storage",
            "container",
            "create",
            "--account-name",
            account_name,
            "--name",
            probe,
            "--auth-mode",
            "login",
            "--output",
            "none",
            check=False,
        )
        if create_result.returncode == 0:
            # Verify the probe container actually exists.
            show_result = az(
                "storage",
                "container",
                "show",
                "--account-name",
                account_name,
                "--name",
                probe,
                "--auth-mode",
                "login",
                "--output",
                "none",
                check=False,
            )
            # Clean up the probe regardless of outcome.
            az(
                "storage",
                "container",
                "delete",
                "--account-name",
                account_name,
                "--name",
                probe,
                "--auth-mode",
                "login",
                "--output",
                "none",
                check=False,
            )
            if show_result.returncode == 0:
                logger.info("RBAC propagation confirmed.")
                return

        if time.monotonic() >= deadline:
            raise RuntimeError(
                f"RBAC did not propagate within {timeout}s for "
                f"account '{account_name}'."
            )
        logger.info("Waiting for RBAC propagation (retrying in %ds) ...", interval)
        time.sleep(interval)


def create_status_storage(
    resource_group: str,
    location: str,
    run_id: str,
    stage_name: str,
) -> tuple[str, str]:
    """Create a transient storage account and container for status blobs.

    Assigns Storage Blob Data Contributor to the current identity so that
    ``--auth-mode login`` works for container/blob operations and
    user-delegation SAS generation.

    Returns (account_name, container_name).
    """
    import os
    import time

    # Azure storage account names: 3-24 chars, lowercase alphanumeric only.
    clean_id = run_id.replace("-", "")[:10]
    account_name = f"fxst{clean_id}"
    container_name = f"{stage_name}-{clean_id}"

    logger.info(
        "Creating status storage account %s in %s ...",
        account_name,
        resource_group,
    )
    az(
        "storage",
        "account",
        "create",
        "--resource-group",
        resource_group,
        "--name",
        account_name,
        "--location",
        location,
        "--sku",
        "Standard_LRS",
        "--output",
        "none",
    )

    # Assign Storage Blob Data Contributor to the current identity.
    storage_account_id = az_query(
        "storage",
        "account",
        "show",
        "--name",
        account_name,
        "--resource-group",
        resource_group,
        query="id",
    )

    client_id = os.environ.get("AZURE_CLIENT_ID")
    if client_id:
        principal_id = az_query("ad", "sp", "show", "--id", client_id, query="id")
        principal_type = "ServicePrincipal"
    else:
        principal_id = az_query("ad", "signed-in-user", "show", query="id")
        principal_type = "User"

    logger.info("Assigning Storage Blob Data Contributor to %s ...", principal_id)
    az(
        "role",
        "assignment",
        "create",
        "--assignee-object-id",
        principal_id,
        "--assignee-principal-type",
        principal_type,
        "--role",
        "Storage Blob Data Contributor",
        "--scope",
        storage_account_id,
        "--output",
        "none",
        check=False,
    )

    # Wait for RBAC to propagate — ``--auth-mode login`` operations silently
    # fail (exit 0, container not created) until the role is active.
    _wait_for_rbac(account_name, container_name)

    logger.info("Deleting status container %s if it exists ...", container_name)
    az(
        "storage",
        "container",
        "delete",
        "--account-name",
        account_name,
        "--name",
        container_name,
        "--auth-mode",
        "login",
        "--output",
        "none",
        check=False,
    )

    # Azure may take up to 30s to fully delete a container. Retry the
    # create+verify cycle — ``az storage container create --auth-mode login``
    # can return exit 0 without actually creating the container when RBAC
    # hasn't fully propagated.
    logger.info("Creating status container %s ...", container_name)
    max_retries = 6
    for attempt in range(1, max_retries + 1):
        result = az(
            "storage",
            "container",
            "create",
            "--account-name",
            account_name,
            "--name",
            container_name,
            "--auth-mode",
            "login",
            "--output",
            "none",
            check=False,
        )
        if result.returncode != 0:
            if attempt < max_retries:
                logger.warning(
                    "Container create failed (attempt %d/%d), retrying in 10s...",
                    attempt,
                    max_retries,
                )
                time.sleep(10)
                continue
            raise RuntimeError(
                f"Failed to create container '{container_name}' after "
                f"{max_retries} attempts."
            )

        # Verify the container actually exists.
        verify_result = az(
            "storage",
            "container",
            "show",
            "--account-name",
            account_name,
            "--name",
            container_name,
            "--auth-mode",
            "login",
            "--output",
            "none",
            check=False,
        )
        if verify_result.returncode == 0:
            break
        # Create returned 0 but container doesn't exist — RBAC flakiness.
        if attempt < max_retries:
            logger.warning(
                "Container create returned 0 but verify failed "
                "(attempt %d/%d), retrying in 10s...",
                attempt,
                max_retries,
            )
            time.sleep(10)
        else:
            raise RuntimeError(
                f"Container '{container_name}' was not found after "
                f"{max_retries} create attempts on account "
                f"'{account_name}'. This may indicate an RBAC "
                f"propagation issue."
            )

    logger.info("Container %s verified.", container_name)

    return account_name, container_name


def generate_container_sas(
    account_name: str,
    container_name: str,
    expiry_hours: int = 6,
) -> str:
    """Generate a write-only container SAS URL.

    Returns the container URL with SAS query string appended. Callers
    append ``/<blob-name>`` to upload individual blobs.
    """
    from datetime import datetime, timedelta, timezone

    expiry = (datetime.now(timezone.utc) + timedelta(hours=expiry_hours)).strftime(
        "%Y-%m-%dT%H:%MZ"
    )

    result = az(
        "storage",
        "container",
        "generate-sas",
        "--account-name",
        account_name,
        "--name",
        container_name,
        "--permissions",
        "cw",
        "--expiry",
        expiry,
        "--auth-mode",
        "login",
        "--as-user",
        "--https-only",
        "--output",
        "tsv",
        capture=True,
    )
    sas_token = result.stdout.strip()

    container_url = f"https://{account_name}.blob.core.windows.net/{container_name}"
    return f"{container_url}?{sas_token}"


def wait_for_cloud_init(
    account_name: str,
    container_name: str,
    blob_name: str,
    timeout: int = 3000,
) -> None:
    """Wait for cloud-init to complete by polling for a status blob.

    The post-cloud-init service uploads a JSON blob with the cloud-init
    status. This function polls for the blob's existence, then downloads
    and validates it. Raises if cloud-init reported an error.
    """
    import json
    import tempfile
    import time

    logger.info(
        "Waiting for cloud-init status blob: %s/%s/%s ...",
        account_name,
        container_name,
        blob_name,
    )
    elapsed = 0
    while elapsed < timeout:
        result = az(
            "storage",
            "blob",
            "exists",
            "--account-name",
            account_name,
            "--container-name",
            container_name,
            "--name",
            blob_name,
            "--auth-mode",
            "login",
            "--output",
            "tsv",
            capture=True,
            check=False,
        )

        exists = result.stdout.strip().lower() == "true"
        if not exists:
            logger.info(
                "Status blob not yet available (%ds elapsed), retrying...",
                elapsed,
            )
            time.sleep(30)
            elapsed += 30
            continue

        # Blob exists — download to a temp file and check status.
        logger.info("Status blob found. Downloading...")
        with tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=True) as tmp:
            az(
                "storage",
                "blob",
                "download",
                "--account-name",
                account_name,
                "--container-name",
                container_name,
                "--name",
                blob_name,
                "--auth-mode",
                "login",
                "--file",
                tmp.name,
                "--output",
                "none",
            )
            content = Path(tmp.name).read_text().strip()

        status = "unknown"
        detail = ""
        try:
            data = json.loads(content)
            status = data.get("status", "unknown")
            detail = data.get("detail", "")
        except (json.JSONDecodeError, TypeError):
            logger.warning("Could not parse status blob: %s", content[:200])

        if status == "completed":
            logger.info("cloud-init completed successfully.")
            return
        elif status == "error":
            raise RuntimeError(f"cloud-init failed: {detail or 'see cloud-init logs'}")
        else:
            raise RuntimeError(
                f"Unexpected cloud-init status: {status!r}. "
                f"Blob content: {content[:200]}"
            )

    raise TimeoutError(
        f"cloud-init status blob not found within {timeout}s: "
        f"{account_name}/{container_name}/{blob_name}"
    )


# -- VM utilities ---------------------------------------------------------


def generate_ssh_keys(generated_dir: Path, name: str) -> tuple[Path, Path]:
    """Generate an SSH key pair in the generated directory.

    Returns (private_key_path, public_key_path).
    """
    generated_dir.mkdir(parents=True, exist_ok=True)
    private_key = generated_dir / f"{name}-ssh.pem"
    public_key = generated_dir / f"{name}-ssh.pub"

    if private_key.exists() and public_key.exists():
        logger.info("Reusing existing SSH keys from %s", generated_dir)
    else:
        logger.info("Generating SSH key pair in %s...", generated_dir)
        run(
            [
                "ssh-keygen",
                "-t",
                "rsa",
                "-b",
                "4096",
                "-f",
                str(private_key),
                "-N",
                "",
                "-q",
            ],
        )
        # ssh-keygen creates <name>.pub; rename to our convention.
        generated_pub = Path(f"{private_key}.pub")
        if generated_pub.exists():
            generated_pub.rename(public_key)

    private_key.chmod(0o600)
    return private_key, public_key


def cleanup_existing_vm(resource_group: str, vm_name: str) -> None:
    """Delete a VM and its associated resources if it exists."""
    result = az(
        "vm",
        "show",
        "--resource-group",
        resource_group,
        "--name",
        vm_name,
        "--output",
        "none",
        check=False,
        capture=True,
    )
    if result.returncode != 0:
        return

    logger.info("Cleaning up existing VM: %s", vm_name)
    az(
        "vm",
        "delete",
        "--resource-group",
        resource_group,
        "--name",
        vm_name,
        "--yes",
        "--force-deletion",
        "true",
        "--output",
        "none",
        check=False,
    )
    logger.info("Existing VM %s deleted.", vm_name)


def download_cloud_init_logs(
    account_name: str,
    container_name: str,
    output_dir: Path,
) -> None:
    """Download all blobs from the status container using azcopy."""
    output_dir.mkdir(parents=True, exist_ok=True)

    container_url = f"https://{account_name}.blob.core.windows.net/{container_name}"

    logger.info(
        "Downloading blobs from %s/%s to %s ...",
        account_name,
        container_name,
        output_dir,
    )
    azcopy(
        "copy",
        f"{container_url}/*",
        str(output_dir),
        "--recursive",
        env={"AZCOPY_AUTO_LOGIN_TYPE": "AZCLI"},
        check=False,
    )
    logger.info("Cloud-init logs downloaded to %s", output_dir)


def export_boot_diagnostics(
    resource_group: str,
    vm_name: str,
    output_dir: Path,
) -> None:
    """Export Azure boot diagnostics (serial console log) for a VM."""
    output_dir.mkdir(parents=True, exist_ok=True)
    log_path = output_dir / "boot-diagnostics.log"

    logger.info("Exporting boot diagnostics for %s ...", vm_name)
    result = az(
        "vm",
        "boot-diagnostics",
        "get-boot-log",
        "--resource-group",
        resource_group,
        "--name",
        vm_name,
        capture=True,
        check=False,
    )

    if result.returncode == 0:
        log_path.write_text(result.stdout)
        logger.info("Boot diagnostics saved to %s", log_path)
    else:
        stderr = result.stderr.strip() if result.stderr else ""
        log_path.write_text(
            f"[boot diagnostics unavailable: rc={result.returncode}]\n{stderr}"
        )
        logger.warning(
            "Failed to retrieve boot diagnostics for %s: %s",
            vm_name,
            stderr or f"rc={result.returncode}",
        )


def ensure_resource_group(resource_group: str, location: str) -> None:
    """Create the resource group only if it does not already exist."""
    result = az(
        "group",
        "show",
        "--name",
        resource_group,
        "--output",
        "none",
        check=False,
        capture=True,
    )
    if result.returncode == 0:
        logger.info("Resource group '%s' already exists.", resource_group)
        return

    logger.info("Creating resource group: %s (location: %s)", resource_group, location)
    az(
        "group",
        "create",
        "--name",
        resource_group,
        "--location",
        location,
        "--output",
        "none",
    )


def ensure_storage_account(
    storage_account: str, resource_group: str, location: str
) -> None:
    """Create the storage account only if it does not already exist."""
    result = az(
        "storage",
        "account",
        "show",
        "--name",
        storage_account,
        "--resource-group",
        resource_group,
        "--output",
        "none",
        check=False,
        capture=True,
    )
    if result.returncode == 0:
        logger.info("Storage account '%s' already exists.", storage_account)
        return

    logger.info(
        "Creating storage account: %s (location: %s)", storage_account, location
    )
    az(
        "storage",
        "account",
        "create",
        "--resource-group",
        resource_group,
        "--name",
        storage_account,
        "--location",
        location,
        "--sku",
        "Standard_LRS",
        "--output",
        "none",
    )


def ensure_gallery(gallery_name: str, resource_group: str, location: str) -> None:
    """Create the gallery only if it does not already exist."""
    result = az(
        "sig",
        "show",
        "--resource-group",
        resource_group,
        "--gallery-name",
        gallery_name,
        "--output",
        "none",
        check=False,
        capture=True,
    )
    if result.returncode == 0:
        logger.info("Gallery '%s' already exists.", gallery_name)
        return

    logger.info("Creating gallery: %s (location: %s)", gallery_name, location)
    az(
        "sig",
        "create",
        "--resource-group",
        resource_group,
        "--gallery-name",
        gallery_name,
        "--location",
        location,
        "--output",
        "none",
    )


def ensure_image_definition(
    gallery_name: str, image_name: str, resource_group: str, location: str
) -> None:
    """Create the image definition only if it does not already exist."""
    result = az(
        "sig",
        "image-definition",
        "show",
        "--resource-group",
        resource_group,
        "--gallery-name",
        gallery_name,
        "--gallery-image-definition",
        image_name,
        "--output",
        "none",
        check=False,
        capture=True,
    )
    if result.returncode == 0:
        logger.info("Image definition '%s' already exists.", image_name)
        return

    logger.info("Creating image definition: %s (location: %s)", image_name, location)
    az(
        "sig",
        "image-definition",
        "create",
        "--resource-group",
        resource_group,
        "--gallery-name",
        gallery_name,
        "--gallery-image-definition",
        image_name,
        "--publisher",
        "cleanroom",
        "--offer",
        "cleanroom-flexnode",
        "--sku",
        "ubuntu-22.04",
        "--os-type",
        "Linux",
        "--os-state",
        "Generalized",
        "--hyper-v-generation",
        "V2",
        "--location",
        location,
        "--features",
        "SecurityType=ConfidentialVmSupported",
        "--output",
        "none",
    )
