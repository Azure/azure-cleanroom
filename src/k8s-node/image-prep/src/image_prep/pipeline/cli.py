# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

"""Click CLI for the image-prep pipeline."""

from pathlib import Path

import click

from image_prep.pipeline.common import setup_logging
from image_prep.pipeline.config import PipelineConfig


def _config_from_ctx(ctx: click.Context) -> PipelineConfig:
    """Build a PipelineConfig from Click context params.

    Non-None CLI values override env vars and defaults.
    """
    overrides = {k: v for k, v in ctx.params.items() if v is not None}
    # Remove keys that are not PipelineConfig fields.
    config_fields = set(PipelineConfig.model_fields.keys())
    overrides = {k: v for k, v in overrides.items() if k in config_fields}
    return PipelineConfig(**overrides)


# -- Shared options -------------------------------------------------------

_common_options = [
    click.option("--resource-group", default=None, help="Azure resource group."),
    click.option(
        "--gallery-resource-group",
        default=None,
        help="Resource group for the gallery (defaults to --resource-group).",
    ),
    click.option("--location", default=None, help="Azure region."),
    click.option(
        "--generated-dir",
        default=None,
        type=click.Path(path_type=Path),
        help="Directory for generated artifacts (SSH keys, etc.).",
    ),
    click.option(
        "--run-id", default=None, help="Unique run ID for status blob container."
    ),
]


def common_options(func):
    """Apply common CLI options to a command."""
    for option in reversed(_common_options):
        func = option(func)
    return func


# -- CLI group ------------------------------------------------------------


@click.group()
def cli():
    """Flex Node image preparation pipeline."""
    setup_logging()


# -- Stage 1: prepare ------------------------------------------------------


@cli.command()
@common_options
@click.option(
    "--vars",
    "vars_file",
    required=True,
    type=click.Path(exists=True, path_type=Path),
    help="Path to vars.yaml.",
)
@click.option("--builder-vm-size", default=None, help="Builder VM size.")
@click.option("--base-image", default=None, help="Base VM image URN.")
def prepare(vars_file, **kwargs):
    """Stage 1: Create builder VM, bake software, generalize."""
    config = _config_from_ctx(click.get_current_context())

    from image_prep.pipeline.prepare import run_prepare

    os_disk_id = run_prepare(config, vars_file)
    click.echo(f"OS disk ID: {os_disk_id}")


# -- Stage 2: integrity-protect --------------------------------------------


@cli.command("integrity-protect")
@common_options
@click.option(
    "--os-disk-id",
    required=True,
    help="OS disk resource ID from stage 1.",
)
@click.option("--integrity-vm-size", default=None, help="Integrity VM size.")
@click.option("--base-image", default=None, help="Base VM image URN.")
def integrity_protect(os_disk_id, **kwargs):
    """Stage 2: Apply integrity protection via cvmboot."""
    config = _config_from_ctx(click.get_current_context())

    from image_prep.pipeline.integrity_protect import run_integrity_protect

    protected_disk_id = run_integrity_protect(config, os_disk_id)
    click.echo(f"Integrity Protected OS disk ID: {protected_disk_id}")


# -- Stage 3: publish -------------------------------------------------------


@cli.command()
@common_options
@click.option(
    "--os-disk-id",
    required=True,
    help="Integrity Protected OS disk resource ID.",
)
@click.option("--gallery-name", default=None, help="Azure Compute Gallery name.")
@click.option("--image-name", default=None, help="Gallery image definition name.")
@click.option("--image-version", default=None, help="Image version.")
@click.option("--storage-account", default=None, help="Storage account for VHD blob.")
def publish(os_disk_id, **kwargs):
    """Stage 3: Export disk to VHD and create gallery image."""
    config = _config_from_ctx(click.get_current_context())

    from image_prep.pipeline.publish import run_publish

    image_version_id = run_publish(config, os_disk_id)
    click.echo(f"Image version ID: {image_version_id}")


# -- All stages -------------------------------------------------------------


@cli.command()
@common_options
@click.option(
    "--vars",
    "vars_file",
    required=True,
    type=click.Path(exists=True, path_type=Path),
    help="Path to vars.yaml.",
)
@click.option("--builder-vm-size", default=None)
@click.option("--integrity-vm-size", default=None)
@click.option("--base-image", default=None)
@click.option("--gallery-name", default=None)
@click.option("--image-name", default=None)
@click.option("--image-version", default=None)
@click.option("--storage-account", default=None)
def all(vars_file, **kwargs):
    """Run all pipeline stages: prepare -> integrity-protect -> publish."""
    config = _config_from_ctx(click.get_current_context())

    # Stage 1: prepare.
    from image_prep.pipeline.prepare import run_prepare

    os_disk_id = run_prepare(config, vars_file)

    # # Stage 2: integrity-protect.
    # from image_prep.pipeline.integrity_protect import run_integrity_protect
    # protected_disk_id = run_integrity_protect(config, os_disk_id)
    protected_disk_id = os_disk_id

    # Stage 3: publish.
    from image_prep.pipeline.publish import run_publish

    image_version_id = run_publish(config, protected_disk_id)

    click.echo(f"\nPipeline complete!")
    click.echo(f"Image version ID: {image_version_id}")


def main():
    cli()


if __name__ == "__main__":
    main()
