# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Stage 3: Publish -- export disk to VHD and create gallery image."""

import logging
import os

from image_prep.pipeline.common import (
    az,
    az_query,
    azcopy,
    ensure_gallery,
    ensure_image_definition,
    ensure_resource_group,
    ensure_storage_account,
)
from image_prep.pipeline.config import PipelineConfig

logger = logging.getLogger("image-prep")


def run_publish(config: PipelineConfig, os_disk_id: str) -> str:
    """Run stage 3 and return the gallery image version ID."""
    logger.info("Stage 3: Publish")
    logger.info("Integrity Protected OS disk: %s", os_disk_id)

    # Gallery and storage account live in their own resource group
    # (persists across builds). Builder resources (VM, disk) live in
    # config.resource_group (transient, deleted after build).
    gallery_rg = config.gallery_resource_group or config.resource_group

    # Ensure the gallery resource group exists.
    ensure_resource_group(gallery_rg, config.location)

    # Ensure the gallery resource group exists.
    ensure_resource_group(gallery_rg, config.location)

    disk_name = os_disk_id.rsplit("/", 1)[-1]

    # Grant SAS access to the disk.
    logger.info("Granting SAS access to disk: %s", disk_name)
    disk_sas_url = az_query(
        "disk",
        "grant-access",
        "--duration-in-seconds",
        "3600",
        "--name",
        disk_name,
        "--resource-group",
        config.resource_group,
        query="accessSAS",
    )

    # Storage account lives in the gallery RG (persists across builds).
    storage_account = config.storage_account
    if not storage_account:
        raise RuntimeError(
            "storage_account is required. Set IMAGE_PREP_STORAGE_ACCOUNT "
            "or pass --storage-account."
        )
    container_name = "vhds"
    vhd_blob_name = f"{config.image_name}-{config.image_version}.vhd"

    ensure_storage_account(storage_account, gallery_rg, config.location)

    logger.info("Creating storage container: %s", container_name)
    az(
        "storage",
        "container",
        "create",
        "--name",
        container_name,
        "--account-name",
        storage_account,
        "--auth-mode",
        "login",
        "--output",
        "none",
        check=False,
    )

    blob_url = (
        f"https://{storage_account}.blob.core.windows.net"
        f"/{container_name}/{vhd_blob_name}"
    )

    # Ensure blob write access for azcopy via Azure AD.
    logger.info("Ensuring blob write access...")
    principal_id, principal_type = _get_current_principal()
    storage_account_id = az_query(
        "storage",
        "account",
        "show",
        "--name",
        storage_account,
        "--resource-group",
        gallery_rg,
        query="id",
    )
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

    # Copy disk to VHD blob. Retry to allow RBAC propagation (can take
    # up to a few minutes after role assignment creation).
    logger.info("Copying disk to VHD blob (this may take several minutes)...")
    import time

    max_retries = 5
    for attempt in range(1, max_retries + 1):
        result = azcopy(
            "copy",
            disk_sas_url,
            blob_url,
            env={"AZCOPY_AUTO_LOGIN_TYPE": "AZCLI"},
            check=False,
        )
        if result.returncode == 0:
            break
        if attempt < max_retries:
            logger.warning(
                "azcopy failed (attempt %d/%d), retrying in 60s "
                "(RBAC propagation delay)...",
                attempt,
                max_retries,
            )
            time.sleep(60)
        else:
            raise RuntimeError(
                f"azcopy failed after {max_retries} attempts. "
                f"Check storage account permissions."
            )

    # Revoke SAS access.
    az(
        "disk",
        "revoke-access",
        "--name",
        disk_name,
        "--resource-group",
        config.resource_group,
        "--output",
        "none",
    )

    # Create gallery and image definition.
    ensure_gallery(config.gallery_name, gallery_rg, config.location)

    ensure_image_definition(
        config.gallery_name,
        config.image_name,
        gallery_rg,
        config.location,
    )

    # Create image version from VHD blob.
    # Target regions must always include config.location (Azure requirement).
    locations = config.target_location_list or []
    if config.location not in locations:
        locations = [config.location] + locations
    logger.info(
        "Creating image version: %s (target regions: %s)",
        config.image_version,
        ", ".join(locations),
    )
    az(
        "sig",
        "image-version",
        "create",
        "--resource-group",
        gallery_rg,
        "--gallery-name",
        config.gallery_name,
        "--gallery-image-definition",
        config.image_name,
        "--gallery-image-version",
        config.image_version,
        "--os-vhd-storage-account",
        storage_account_id,
        "--os-vhd-uri",
        blob_url,
        "--location",
        config.location,
        "--target-regions",
        *locations,
        "--output",
        "none",
    )

    image_version_id = az_query(
        "sig",
        "image-version",
        "show",
        "--resource-group",
        gallery_rg,
        "--gallery-name",
        config.gallery_name,
        "--gallery-image-definition",
        config.image_name,
        "--gallery-image-version",
        config.image_version,
        query="id",
    )

    logger.info("Stage 3 complete. Image version: %s", image_version_id)
    return image_version_id


def _get_current_principal() -> tuple[str, str]:
    """Return (object_id, principal_type) for the current Azure identity.

    Uses the AZURE_CLIENT_ID env var when running as a service principal
    or managed identity. Falls back to the signed-in user otherwise.
    """
    client_id = os.environ.get("AZURE_CLIENT_ID")
    if client_id:
        object_id = az_query("ad", "sp", "show", "--id", client_id, query="id")
        return object_id, "ServicePrincipal"

    object_id = az_query("ad", "signed-in-user", "show", query="id")
    return object_id, "User"
