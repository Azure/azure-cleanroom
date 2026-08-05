# pylint: disable=missing-function-docstring
# pylint: disable=missing-module-docstring

import os

from azure.cli.core.util import CLIError
from knack.log import get_logger

logger = get_logger(__name__)

DEFAULT_KUBECTL_CLEANROOM_IMAGE = "kubectl-cleanroom-binary"


def _read_env_file(env_file):
    """Read key=value pairs from an env file, returning a dict."""
    env_vars = {}
    if not env_file:
        return env_vars
    if not os.path.exists(env_file):
        raise CLIError(f"Environment file not found: {env_file}")
    with open(env_file, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, value = line.split("=", 1)
                env_vars[key.strip()] = value.strip()
    return env_vars


def _resolve_source_image(source, env_file, client_version):
    """Resolve the full OCI artifact reference for kubectl-cleanroom."""
    env_vars = _read_env_file(env_file)

    # If client_version was not explicitly set, try the env file.
    if client_version == "latest":
        env_tag = env_vars.get(
            "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_TAG"
        )
        if env_tag:
            client_version = env_tag

    if source:
        return f"{source}/{DEFAULT_KUBECTL_CLEANROOM_IMAGE}:{client_version}"

    # Try to get registry URL from the env file.
    registry_url = env_vars.get(
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL"
    )
    if registry_url:
        return f"{registry_url}/{DEFAULT_KUBECTL_CLEANROOM_IMAGE}:{client_version}"

    raise CLIError(
        "Cannot determine the OCI artifact source. "
        "Provide --source or pass --env-file containing "
        "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL."
    )


def operator_install_cli(
    cmd, install_location, client_version, source=None, env_file=None
):
    import shutil
    import stat
    import subprocess
    import tempfile

    artifact = _resolve_source_image(source, env_file, client_version)
    logger.warning("Downloading kubectl-cleanroom from %s ...", artifact)

    with tempfile.TemporaryDirectory() as tmpdir:
        try:
            subprocess.check_call(
                ["oras", "pull", artifact, "--output", tmpdir],
            )
        except FileNotFoundError as e:
            raise CLIError(
                "oras CLI not found. Install it from "
                "https://oras.land/docs/installation"
            ) from e
        except subprocess.CalledProcessError as e:
            raise CLIError(f"Failed to pull OCI artifact {artifact}: {e}") from e

        tmp_binary = os.path.join(tmpdir, "kubectl-cleanroom")
        if not os.path.exists(tmp_binary):
            raise CLIError(
                f"kubectl-cleanroom binary not found in artifact {artifact}."
            )

        # Ensure the target directory exists.
        install_dir = os.path.dirname(install_location)
        if install_dir:
            os.makedirs(install_dir, exist_ok=True)

        try:
            shutil.move(tmp_binary, install_location)
            os.chmod(
                install_location,
                os.stat(install_location).st_mode
                | stat.S_IXUSR
                | stat.S_IXGRP
                | stat.S_IXOTH,
            )
        except PermissionError as e:
            raise CLIError(
                f"Permission denied writing to {install_location}. "
                f"Try with sudo or use --install-location to "
                f"specify a writable path."
            ) from e

    logger.warning("kubectl-cleanroom installed to %s", install_location)

    # Verify it's on PATH.
    if not shutil.which("kubectl-cleanroom"):
        logger.warning(
            "WARNING: %s is not on your PATH. "
            "Add it or use --install-location to install to "
            "a directory on your PATH.",
            install_location,
        )
