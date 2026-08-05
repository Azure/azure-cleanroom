"""Utility functions for Kubernetes container manipulation."""

from typing import List, Optional

from kubernetes.client import models as k8smodels

from ..utilities.constants import Constants


def find_container(
    containers: List[k8smodels.V1Container], name: str
) -> k8smodels.V1Container:
    """Find a container by name, raising ValueError if not found."""
    return next(c for c in containers if c.name == name)


def find_container_optional(
    containers: Optional[List[k8smodels.V1Container]], name: str
) -> Optional[k8smodels.V1Container]:
    """Find a container by name, returning None if not found."""
    if not containers:
        return None
    return next((c for c in containers if c.name == name), None)


def set_mount_propagation(
    containers: Optional[List[k8smodels.V1Container]],
    volume_name: str,
    mode: str,
    *,
    blobfuse_mode: Optional[str] = None,
):
    """Set mount propagation on all containers for a given volume.

    Args:
        containers: List of containers to update.
        volume_name: Volume mount name to match.
        mode: Default mount propagation mode.
        blobfuse_mode: If set, blobfuse containers use this mode instead.
    """
    if not containers:
        return
    for container in containers:
        if not container.volume_mounts:
            continue
        for vm in container.volume_mounts:
            if vm.name == volume_name:
                if (
                    blobfuse_mode
                    and container.name
                    and Constants.BLOBFUSE_CONTAINER_SUFFIX in container.name
                ):
                    vm.mount_propagation = blobfuse_mode
                else:
                    vm.mount_propagation = mode


def add_env_var(container: k8smodels.V1Container, name: str, value: str):
    """Set an environment variable on a container (upsert)."""
    if container.env is None:
        container.env = []
    for env in container.env:
        if env.name == name:
            env.value = value
            return
    container.env.append(k8smodels.V1EnvVar(name=name, value=value))


def prepend_env_path(container: k8smodels.V1Container, name: str, value: str):
    """Prepend a value to a colon-separated env var (e.g. LD_LIBRARY_PATH)."""
    if container.env is None:
        container.env = []
    for env in container.env:
        if env.name == name:
            env.value = f"{value}:{env.value}"
            return
    container.env.append(k8smodels.V1EnvVar(name=name, value=value))


def add_volume_mount(
    container: k8smodels.V1Container,
    name: str,
    mount_path: str,
    read_only: bool = False,
):
    """Append a volume mount to a container."""
    if container.volume_mounts is None:
        container.volume_mounts = []
    container.volume_mounts.append(
        k8smodels.V1VolumeMount(name=name, mount_path=mount_path, read_only=read_only)
    )


def remove_containers_by_name(
    containers: List[k8smodels.V1Container], name: str
) -> List[k8smodels.V1Container]:
    """Return a new list with the named container removed."""
    return [c for c in containers if c.name != name]


def remove_volume_mounts_by_name(container: k8smodels.V1Container, volume_name: str):
    """Remove volume mounts matching a name from a container."""
    if container.volume_mounts:
        container.volume_mounts = [
            vm for vm in container.volume_mounts if vm.name != volume_name
        ]
