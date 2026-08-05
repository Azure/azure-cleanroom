// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class FlexNodeProfileInput
{
    public const string ModeManual = "manual";
    public const string ModeAuto = "auto";

    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the provisioning mode. "manual" provisions a fixed number
    /// of nodes at cluster creation. "auto" installs Karpenter for on-demand
    /// provisioning. Defaults to "manual".
    /// </summary>
    public string Mode { get; set; } = ModeManual;

    public string? SshPrivateKeyPem { get; set; }

    public string? SshPublicKey { get; set; }

    /// <summary>
    /// Gets or sets the policy signing certificate PEM used for api-server-proxy pod verification.
    /// </summary>
    public string? PolicySigningCertPem { get; set; }

    /// <summary>
    /// Gets or sets the VM size for the flex node. If not specified, defaults to Standard_DC2as_v5.
    /// </summary>
    public string? VmSize { get; set; }

    /// <summary>
    /// Gets or sets the OS disk size in GB for the flex node. If not specified, defaults to
    /// 30 GB for CPU VMs and 128 GB for GPU VMs to accommodate large model weights.
    /// </summary>
    public int? OsDiskSizeInGB { get; set; }

    /// <summary>
    /// Gets or sets the number of flex nodes to create. Defaults to 1.
    /// </summary>
    public int NodeCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the maximum number of pods per flex node. AKS hard limit is 250, but we set
    /// max to 110 to avoid hitting the limit due to buffer IPs and ensure better stability.
    /// </summary>
    public int MaxPodsPerNode { get; set; } = 110;

    /// <summary>
    /// Gets or sets a value indicating whether to enable insecure mode on the api-server-proxy,
    /// bypassing pod policy verification.
    /// </summary>
    public bool Insecure { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to provision using SSH-based setup instead of a
    /// baked VM image. When true, a stock Ubuntu CVM is created and software is installed via SSH
    /// at deploy time. When false (default), a pre-built gallery image with all software baked in
    /// is used.
    /// </summary>
    public bool ProvisionUsingSSH { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to require pre-provisioned Kind
    /// worker nodes instead of dynamically adding them via kindscaler. When true,
    /// the flow fails if the requested nodes are not already present. Only applicable
    /// to virtual (Kind) clusters and when mode is manual. Defaults to false.
    /// </summary>
    public bool RequirePreProvisionedKindNodes { get; set; }

    /// <summary>
    /// Gets or sets the workload-agnostic GPU capability configuration for flex nodes.
    /// When null, GPU nodes use the default full-GPU mode with no sharing.
    /// </summary>
    public FlexNodeGpuConfigInput? Gpu { get; set; }
}

/// <summary>
/// GPU capability configuration for flex nodes. Controls how GPUs are
/// shared across pods. Workload-agnostic — applies to inferencing, analytics,
/// and any other GPU workload scheduled on the flex node.
/// </summary>
public class FlexNodeGpuConfigInput
{
    /// <summary>
    /// Gets or sets the GPU sharing configuration. Defaults to no sharing.
    /// </summary>
    public FlexNodeGpuSharingInput? Sharing { get; set; }
}

/// <summary>
/// GPU sharing configuration. MPS allows multiple pods to share a single GPU
/// with memory and compute partitioning enforced by the MPS control daemon.
/// </summary>
public class FlexNodeGpuSharingInput
{
    /// <summary>
    /// Gets or sets the sharing mode. Supported values: "none", "mps".
    /// </summary>
    public string Mode { get; set; } = "none";

    /// <summary>
    /// Gets or sets the number of MPS replicas per GPU. Each replica gets
    /// an equal fraction of GPU memory and compute. Required when mode is "mps".
    /// </summary>
    public int? Replicas { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether shared GPU resources are
    /// advertised as "nvidia.com/gpu.shared" instead of "nvidia.com/gpu".
    /// Defaults to false to maintain backward compatibility with existing
    /// pod specs that request "nvidia.com/gpu".
    /// </summary>
    public bool RenameByDefault { get; set; } = false;
}