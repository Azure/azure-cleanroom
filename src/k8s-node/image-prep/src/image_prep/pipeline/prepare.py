# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Stage 1: Prepare -- create builder VM, bake software, generalize."""

import logging
from pathlib import Path

from image_prep.pipeline.common import (
    az,
    az_query,
    cleanup_existing_vm,
    create_status_storage,
    download_cloud_init_logs,
    ensure_resource_group,
    export_boot_diagnostics,
    generate_container_sas,
    generate_ssh_keys,
    wait_for_cloud_init,
)
from image_prep.pipeline.config import PipelineConfig
from image_prep.pipeline.render import load_vars, render_template, validate_cloud_init

logger = logging.getLogger("image-prep")

STATUS_BLOB_NAME = "builder-status.json"


def run_prepare(config: PipelineConfig, vars_file: Path) -> str:
    """Run stage 1 and return the OS disk resource ID."""
    logger.info("Stage 1: Prepare")

    private_key, public_key = generate_ssh_keys(
        config.generated_dir, config.builder_vm_name
    )

    # Ensure resource group exists.
    ensure_resource_group(config.resource_group, config.location)

    # Create transient storage for cloud-init status and log blobs.
    account_name, container_name = create_status_storage(
        config.resource_group, config.location, config.run_id, "prepare"
    )
    container_sas_url = generate_container_sas(account_name, container_name)

    # Render cloud-init template with container SAS URL.
    vars_model = load_vars(vars_file)
    rendered_dir = config.generated_dir / "rendered"
    cloud_init_path = render_template(
        "prepare-cloud-init.yaml.j2",
        rendered_dir / "cloud-init.yaml",
        vars_model=vars_model,
        extra_vars={
            "container_sas_url": container_sas_url,
            "status_blob_name": STATUS_BLOB_NAME,
            "post_cloud_init_log_files": [
                ("/var/log/cloud-init-output.log", "cloud-init-output.log"),
                ("/var/log/cloud-init.log", "cloud-init.log"),
                ("/var/lib/cloud/instance/user-data.txt", "cloud-init-user-data.txt"),
            ],
            "post_cloud_init_deprovision": True,
        },
    )
    validate_cloud_init(cloud_init_path)

    # Clean up any existing builder VM from a previous run.
    cleanup_existing_vm(config.resource_group, config.builder_vm_name)

    # Create builder VM.
    logger.info("Creating builder VM: %s", config.builder_vm_name)
    az(
        "vm",
        "create",
        "--resource-group",
        config.resource_group,
        "--name",
        config.builder_vm_name,
        "--location",
        config.location,
        "--image",
        config.base_image,
        "--size",
        config.builder_vm_size,
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

    # Wait for the VM to be provisioned.
    logger.info("Waiting for builder VM provisioning...")
    az(
        "vm",
        "wait",
        "--resource-group",
        config.resource_group,
        "--name",
        config.builder_vm_name,
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
        config.builder_vm_name,
        "--output",
        "none",
    )

    # Wait for cloud-init to complete via status blob.
    # Cloud-init runcmd handles restrict + deprovision before uploading
    # the status blob, so the VM is ready for deallocate once this returns.
    wait_for_cloud_init(account_name, container_name, STATUS_BLOB_NAME)

    # Download cloud-init logs from the status storage container.
    download_cloud_init_logs(
        account_name,
        container_name,
        config.generated_dir / "prepare" / "cloud-init-logs" / config.builder_vm_name,
    )

    # Brief pause to allow serial console buffer to flush before capture.
    import time

    time.sleep(10)

    # Export boot diagnostics (serial console log).
    export_boot_diagnostics(
        config.resource_group,
        config.builder_vm_name,
        config.generated_dir / "prepare" / "boot-diagnostics" / config.builder_vm_name,
    )

    # Deallocate and generalize.
    logger.info("Deallocating builder VM...")
    az(
        "vm",
        "deallocate",
        "--resource-group",
        config.resource_group,
        "--name",
        config.builder_vm_name,
        "--no-wait",
        "--output",
        "none",
    )

    logger.info("Waiting for builder VM to deallocate...")
    az(
        "vm",
        "wait",
        "--resource-group",
        config.resource_group,
        "--name",
        config.builder_vm_name,
        "--custom",
        "instanceView.statuses[?code=='PowerState/deallocated']",
    )

    logger.info("Generalizing builder VM...")
    az(
        "vm",
        "generalize",
        "--resource-group",
        config.resource_group,
        "--name",
        config.builder_vm_name,
        "--output",
        "none",
    )

    # Get the OS disk resource ID.
    os_disk_id = az_query(
        "vm",
        "show",
        "--resource-group",
        config.resource_group,
        "--name",
        config.builder_vm_name,
        query="storageProfile.osDisk.managedDisk.id",
    )

    # Delete the builder VM but keep the OS disk.
    logger.info("Deleting builder VM (keeping OS disk)...")
    az(
        "vm",
        "delete",
        "--resource-group",
        config.resource_group,
        "--name",
        config.builder_vm_name,
        "--yes",
        "--output",
        "none",
    )

    logger.info("Stage 1 complete. OS disk: %s", os_disk_id)
    return os_disk_id
