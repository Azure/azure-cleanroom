# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

from pydantic import BaseModel, Field


class ImagePrepVars(BaseModel):
    """Variables for rendering the Flex Node image-prep cloud-init templates."""

    # AKS Flex Node agent version tag (e.g. "v0.0.19").
    aks_flex_node_version: str = Field(default="v0.0.19")

    # Whether to install NVIDIA GPU drivers in the image. When true the driver
    # packages are installed at bake time but the commands are guarded so they
    # do not fail if the build VM has no GPU hardware.
    install_nvidia_gpu_drivers: bool = Field(default=False)

    # NVIDIA driver version number (e.g. "580").
    nvidia_driver_version: str = Field(default="580")

    # NVIDIA driver branch (e.g. "server-open").
    nvidia_driver_branch: str = Field(default="server-open")

    # OCI registry URL for the api-server-proxy package (pulled via oras).
    api_server_proxy_oci_url: str = Field(
        default="mcr.microsoft.com/azurecleanroom/k8s-node/api-server-proxy:latest"
    )

    # Directory on the image where the api-server-proxy binary and install.sh
    # are placed at bake time.
    api_server_proxy_install_path: str = Field(default="/opt/api-server-proxy")

    # OCI registry URL for the kubelet-proxy package (pulled via oras).
    kubelet_proxy_oci_url: str = Field(
        default="mcr.microsoft.com/azurecleanroom/k8s-node/kubelet-proxy:latest"
    )

    # Directory on the image where the kubelet-proxy binary and install.sh
    # are placed at bake time.
    kubelet_proxy_install_path: str = Field(default="/opt/kubelet-proxy")

    # Directory where boot config files are expected (dropped by
    # cloud-init user-data at VM creation time).
    cleanroom_boot_config_dir: str = Field(default="/etc/cleanroom-boot")

    # Image version string baked into the systemd unit as
    # Environment=IMAGE_VERSION. Used by the init script when writing
    # /var/run/cleanroom/status.json.
    image_version: str = Field(default="0.0.0-dev")

    # OCI registry URL for the cleanroom-boot Go binary (pulled via oras).
    cleanroom_boot_oci_url: str = Field(
        default="mcr.microsoft.com/azurecleanroom/k8s-node/cleanroom-boot:latest"
    )

    # Directory on the image where the cleanroom-boot binary is staged at
    # bake time before copying to /usr/local/bin.
    cleanroom_boot_install_path: str = Field(default="/opt/cleanroom-boot")
