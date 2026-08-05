class Constants:
    """Constants used in the Inference Service."""

    ALLOW_ALL_POLICY_BASE64 = "WyJhbGxvd2FsbCJd"

    # The service name to use for any OpenTelemetry instrumentation.
    OTEL_SERVICE_NAME = "kserve-inferencing-frontend"

    # The environment variable key for OpenTelemetry trace context.
    OTEL_TRACE_CONTEXT_ENV_KEY = "OTEL_TRACE_CONTEXT_BASE64"

    # Container and volume names.
    KSERVE_CONTAINER = "kserve-container"
    REMOTE_MOUNTS_VOLUME = "remotemounts"
    TELEMETRY_MOUNTS_VOLUME = "telemetrymounts"
    VOLUME_STATUS_MOUNTS_VOLUME = "volumestatusmounts"
    SHARED_VOLUME = "shared"
    TPMRM0_VOLUME = "tpmrm0"
    ATTESTATION_LOCK_VOLUME = "attestation-lock"
    NVIDIA_DRIVER_LIBS_VOLUME = "nvidia-driver-libs"

    # Sidecar container names.
    BLOBFUSE_CONTAINER_SUFFIX = "blobfuse"
    CVM_ATTESTATION_AGENT_CONTAINER = "cvm-attestation-agent"
    CCR_GOVERNANCE_CONTAINER = "ccr-governance"

    # Mount paths.
    REMOTE_MOUNT_PATH = "/mnt/remote"
    TPMRM0_HOST_PATH = "/dev/tpmrm0"
    ATTESTATION_LOCK_HOST_PATH = "/run/azure-cleanroom"
    NVIDIA_DRIVER_LIBS_HOST_PATH = "/usr/lib/x86_64-linux-gnu"
    NVIDIA_DRIVER_LIBS_MOUNT_PATH = "/usr/lib/nvidia"

    # OpenSSL 3.4.1 host path and mount path for improved CPU-GPU bandwidth.
    # Installed by install-gpu-driver.sh on GPU CVM nodes.
    OPENSSL_VOLUME = "openssl"
    OPENSSL_HOST_PATH = "/opt/openssl"
    OPENSSL_MOUNT_PATH = "/opt/openssl"
    OPENSSL_LIB_PATH = "/opt/openssl/lib64"

    # Node scheduling.
    POD_POLICY_KEY = "pod-policy"
    POD_POLICY_VALUE = "required"

    # GFD label used to schedule GPU workloads only on nodes where
    # GPU Feature Discovery has identified the GPU hardware.
    GPU_PRODUCT_LABEL_KEY = "nvidia.com/gpu.product"
