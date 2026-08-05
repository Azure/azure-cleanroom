# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Pipeline configuration with environment variable overrides."""

from pathlib import Path

from pydantic import Field
from pydantic_settings import BaseSettings


class PipelineConfig(BaseSettings):
    """Configuration for the image-prep pipeline.

    All settings can be overridden via environment variables with the
    IMAGE_PREP_ prefix (e.g. IMAGE_PREP_RESOURCE_GROUP=myRg).
    """

    model_config = {"env_prefix": "IMAGE_PREP_"}

    # Resource group for builder VMs, disks, and storage accounts.
    # Transient resources are created here and can be deleted after the build.
    resource_group: str = Field(default="flexnode-image-builder")

    # Resource group for the Azure Compute Gallery and image definitions.
    # Defaults to resource_group if not set. Kept across builds so gallery
    # images persist.
    gallery_resource_group: str = Field(default="")

    location: str = Field(default="eastus")

    # Stage 0: render.
    vars_file: Path = Field(default=Path("vars.yaml"))

    # Stage 1: prepare.
    builder_vm_size: str = Field(default="Standard_D4s_v5")
    base_image: str = Field(default="Canonical:ubuntu-24_04-lts:server:latest")

    # Stage 2: integrity-protect.
    integrity_vm_size: str = Field(default="Standard_D4s_v5")

    # Stage 3: publish.
    gallery_name: str = Field(default="AzureCleanroomGallery")
    image_name: str = Field(default="cleanroom-flexnode")
    image_version: str = Field(default="1.0.0")
    storage_account: str = Field(default="")

    # Locations where the gallery image version should be replicated.
    # Defaults to config.location only. Set via IMAGE_PREP_TARGET_LOCATIONS
    # as a comma-separated string (e.g. "germanywestcentral,eastus").
    target_locations: str = Field(default="")

    @property
    def target_location_list(self) -> list[str]:
        """Parse target_locations into a list."""
        if not self.target_locations:
            return []
        return [s.strip() for s in self.target_locations.split(",") if s.strip()]

    # Shared.
    generated_dir: Path = Field(default=Path("generated"))
    run_id: str = Field(default="")

    @property
    def builder_vm_name(self) -> str:
        """Derive builder VM name from run_id."""
        suffix = self.run_id[:16] if self.run_id else "local"
        return f"flexnode-builder-{suffix}"

    @property
    def integrity_vm_name(self) -> str:
        """Derive integrity VM name from run_id."""
        suffix = self.run_id[:16] if self.run_id else "local"
        return f"flexnode-integrity-{suffix}"
