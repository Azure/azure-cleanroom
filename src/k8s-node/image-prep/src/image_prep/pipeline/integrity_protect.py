# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Stage 2: Integrity-protect -- install cvmboot and process the OS disk."""

import logging

from image_prep.pipeline.common import (
    az,
    cleanup_existing_vm,
    create_status_storage,
    download_cloud_init_logs,
    export_boot_diagnostics,
    generate_container_sas,
    generate_ssh_keys,
    wait_for_cloud_init,
)
from image_prep.pipeline.config import PipelineConfig
from image_prep.pipeline.render import render_template

logger = logging.getLogger("image-prep")

STATUS_BLOB_NAME = "integrity-status.json"


def run_integrity_protect(config: PipelineConfig, os_disk_id: str) -> str:
    """Run stage 2 and return the Integrity Protected OS disk resource ID."""
    logger.info("Stage 2: Integrity-protect")
    logger.info("OS disk to protect: %s", os_disk_id)

    private_key, public_key = generate_ssh_keys(
        config.generated_dir, config.integrity_vm_name
    )

    # Create transient storage for cloud-init status and log blobs.
    account_name, container_name = create_status_storage(
        config.resource_group, config.location, config.run_id, "integrity"
    )
    container_sas_url = generate_container_sas(account_name, container_name)

    # Render integrity cloud-init template with container SAS URL.
    rendered_dir = config.generated_dir / "rendered"
    cloud_init_path = render_template(
        "integrity-cloud-init.yaml.j2",
        rendered_dir / "integrity-cloud-init.yaml",
        extra_vars={
            "container_sas_url": container_sas_url,
            "status_blob_name": STATUS_BLOB_NAME,
            "post_cloud_init_log_files": [
                ("/var/log/cloud-init-output.log", "integrity-cloud-init-output.log"),
                ("/var/log/cloud-init.log", "integrity-cloud-init.log"),
            ],
            "post_cloud_init_deprovision": False,
        },
    )

    # Clean up any existing integrity VM from a previous run.
    cleanup_existing_vm(config.resource_group, config.integrity_vm_name)

    # Create integrity VM.
    logger.info("Creating integrity VM: %s", config.integrity_vm_name)
    az(
        "vm",
        "create",
        "--resource-group",
        config.resource_group,
        "--name",
        config.integrity_vm_name,
        "--location",
        config.location,
        "--image",
        config.base_image,
        "--size",
        config.integrity_vm_size,
        "--admin-username",
        "azureuser",
        "--ssh-key-values",
        str(public_key),
        "--custom-data",
        str(cloud_init_path),
        "--public-ip-sku",
        "Standard",
        "--no-wait",
        "--output",
        "none",
    )

    logger.info("Waiting for integrity VM provisioning...")
    az(
        "vm",
        "wait",
        "--resource-group",
        config.resource_group,
        "--name",
        config.integrity_vm_name,
        "--created",
    )

    # Enable managed boot diagnostics for debuggability.
    az(
        "vm",
        "boot-diagnostics",
        "enable",
        "--resource-group",
        config.resource_group,
        "--name",
        config.integrity_vm_name,
        "--output",
        "none",
    )

    # Wait for cloud-init to complete via status blob.
    wait_for_cloud_init(account_name, container_name, STATUS_BLOB_NAME)

    # Download cloud-init logs from the status storage container.
    download_cloud_init_logs(
        account_name,
        container_name,
        config.generated_dir
        / "integrity-protect"
        / "cloud-init-logs"
        / config.integrity_vm_name,
    )

    # Brief pause to allow serial console buffer to flush before capture.
    import time

    time.sleep(10)

    # Export boot diagnostics (serial console log).
    export_boot_diagnostics(
        config.resource_group,
        config.integrity_vm_name,
        config.generated_dir
        / "integrity-protect"
        / "boot-diagnostics"
        / config.integrity_vm_name,
    )

    # Mount stage 1's OS disk as data disk on the integrity VM.
    disk_name = _disk_name_from_id(os_disk_id)
    logger.info("Mounting OS disk '%s' as data disk (LUN 1)...", disk_name)
    az(
        "vm",
        "disk",
        "attach",
        "--resource-group",
        config.resource_group,
        "--vm-name",
        config.integrity_vm_name,
        "--disks",
        os_disk_id,
        "--lun",
        "1",
        "--output",
        "none",
    )

    # Detach the data disk.
    logger.info("Detaching data disk...")
    az(
        "vm",
        "disk",
        "detach",
        "--resource-group",
        config.resource_group,
        "--vm-name",
        config.integrity_vm_name,
        "--name",
        disk_name,
        "--output",
        "none",
    )

    # Clean up the integrity VM.
    logger.info("Deleting integrity VM...")
    az(
        "vm",
        "delete",
        "--resource-group",
        config.resource_group,
        "--name",
        config.integrity_vm_name,
        "--yes",
        "--output",
        "none",
    )

    logger.info("Stage 2 complete. Integrity Protected OS disk: %s", os_disk_id)
    return os_disk_id


def _disk_name_from_id(disk_id: str) -> str:
    """Extract the disk name from an Azure resource ID."""
    return disk_id.rsplit("/", 1)[-1]
