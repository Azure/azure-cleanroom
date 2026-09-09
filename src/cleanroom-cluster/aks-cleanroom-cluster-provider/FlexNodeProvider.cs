// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.ContainerService;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Resources;
using CleanRoomProvider;
using Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AksCleanRoomProvider;

/// <summary>
/// Deploys an Azure IaaS VM and configures it as a flex node to join an AKS cluster.
/// </summary>
public class FlexNodeProvider
{
    private const string FlexNodeTag = "cleanroom-flex-node:cluster-name";
    private const string KubeletMiSuffix = "-flex-kubelet-mi";
    private const string FlexVmPrefix = "-flex-vm-";
    private const string FlexNodeSubnetName = "flexnode";

    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public FlexNodeProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public async Task CreateNodesAsync(
        ArmClient client,
        string clClusterName,
        ResourceGroupResource resourceGroupResource,
        ContainerServiceManagedClusterResource aks,
        string tenantId,
        List<ResourceIdentifier> flexNodeSubnetsIds,
        FlexNodeProfileInput flexNodeProfile,
        KubectlClient kubectlClient,
        ISshSessionFactory sshSessionFactory,
        IReadOnlyList<IPTag> ipTags,
        bool forceCreate,
        IProgress<string> progressReporter)
    {
        string location = aks.Data.Location;
        string subscriptionId = resourceGroupResource.Id.SubscriptionId!;
        string kubeletMiName = clClusterName + KubeletMiSuffix;

        this.logger.LogInformation("Creating kubelet managed identity for flex node...");
        var kubeletMi = await this.CreateManagedIdentityAsync(
            resourceGroupResource,
            clClusterName,
            location,
            kubeletMiName,
            forceCreate);

        this.logger.LogInformation("Assigning role to kubelet managed identity...");
        await this.AssignOwnerRoleToMiAsync(aks, kubeletMi);

        this.logger.LogInformation("Setting up Kubernetes RBAC for kubelet identity...");
        await this.SetupKubernetesRbacAsync(kubeletMi, kubectlClient);

        var configJson = this.GenerateFlexNodeConfig(
            subscriptionId,
            tenantId,
            kubeletMi.Data.ClientId!.Value.ToString(),
            aks.Id.ToString(),
            location,
            flexNodeProfile.MaxPodsPerNode,
            aks.Data.CurrentKubernetesVersion);

        // Save the SSH private key to a temporary file for SSH access.
        var sshPrivateKeyPath = Path.Combine("/tmp", $"{clClusterName}-flex-sshkey.pem");
        await File.WriteAllTextAsync(sshPrivateKeyPath, flexNodeProfile.SshPrivateKeyPem);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                sshPrivateKeyPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        this.logger.LogInformation($"SSH private key saved to: {sshPrivateKeyPath}");

        string? flexNodeImageId = null;
        if (!flexNodeProfile.ProvisionUsingSSH)
        {
            flexNodeImageId = await ImageUtils.GetFlexNodeImageId(
                this.logger,
                this.configuration);
            progressReporter.Report($"Creating flex nodes using baked image ID: {flexNodeImageId}");
            this.logger.LogInformation($"Flex node image ID: {flexNodeImageId}");
        }
        else
        {
            progressReporter.Report("Creating flex nodes using SSH-based setup " +
                "(stock Ubuntu CVM image)");
            this.logger.LogInformation("Using SSH-based setup (stock Ubuntu CVM image).");
        }

        var createTasks = new List<Task>();
        for (int i = 0; i < flexNodeProfile.NodeCount; i++)
        {
            int ordinal = i;
            createTasks.Add(Task.Run(async () =>
            {
                string vmName = clClusterName + FlexVmPrefix + ordinal;
                int subnetIndex = ordinal / FlexNodeIpLayout.NodesPerSubnet;
                int nodeOrdinalInSubnet = ordinal % FlexNodeIpLayout.NodesPerSubnet;
                string subnetName = $"{FlexNodeSubnetName}-{subnetIndex}";
                if (subnetIndex >= flexNodeSubnetsIds.Count)
                {
                    throw new InvalidOperationException(
                        $"Subnet '{subnetName}' not found in the provided flex node " +
                        $"subnets. Available subnets: " +
                        $"{string.Join(", ", flexNodeSubnetsIds)}.");
                }

                var flexNodeSubnetId = flexNodeSubnetsIds[subnetIndex];

                if (await kubectlClient.IsFlexNodeReadyAsync(vmName))
                {
                    this.logger.LogInformation(
                        $"Flex node '{vmName}' already has ready label. Skipping creation.");
                    progressReporter.Report(
                        $"Flex node setup already completed on VM {vmName}...");
                    return;
                }

                this.logger.LogInformation($"Creating flex node VM '{vmName}'...");
                var nodeIpInfo = FlexNodeIpLayout.GetNodeIpInfo(
                    subnetIndex, nodeOrdinalInSubnet);

                string vmSizeValue = flexNodeProfile.VmSize ?? "Standard_DC2as_v5";

                if (flexNodeProfile.ProvisionUsingSSH)
                {
                    await this.CreateNodeViaSshAsync(
                        resourceGroupResource,
                        clClusterName,
                        vmName,
                        location,
                        flexNodeSubnetId,
                        nodeIpInfo,
                        kubeletMi,
                        flexNodeProfile,
                        vmSizeValue,
                        configJson,
                        sshPrivateKeyPath,
                        kubectlClient,
                        sshSessionFactory,
                        ipTags,
                        forceCreate,
                        progressReporter);
                }
                else
                {
                    await this.CreateNodeViaImageAsync(
                        resourceGroupResource,
                        clClusterName,
                        vmName,
                        location,
                        flexNodeSubnetId,
                        nodeIpInfo,
                        kubeletMi,
                        flexNodeProfile,
                        vmSizeValue,
                        configJson,
                        flexNodeImageId!,
                        kubectlClient,
                        ipTags,
                        forceCreate);
                }
            }));
        }

        await Task.WhenAll(createTasks);
    }

    public async Task<List<FlexNode>> GetNodesAsync(
        KubectlClient kubectlClient)
    {
        var nodeObjects = await kubectlClient.GetFlexNodesAsync();
        var flexNodes = new List<FlexNode>();
        foreach (var node in nodeObjects)
        {
            flexNodes.Add(new FlexNode
            {
                K8sNodeDetails = node
            });
        }

        return flexNodes;
    }

    internal static bool IsGpuVmSize(string vmSize)
    {
        return vmSize.StartsWith("Standard_NC", StringComparison.OrdinalIgnoreCase) ||
            vmSize.StartsWith("Standard_ND", StringComparison.OrdinalIgnoreCase) ||
            vmSize.StartsWith("Standard_NV", StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetCloudInitYaml(
        FlexNodeIpInfo nodeIpInfo,
        string configJson,
        string signingCertPem,
        bool insecure,
        FlexNodeGpuConfigInput? gpuConfig = null)
    {
        // Read the api-policy file based on insecure mode.
        string apiPolicyFileName = insecure
            ? "insecure-api-policy.json"
            : "default-api-policy.json";
        string apiPolicyFilePath = Path.Combine(
            AppContext.BaseDirectory,
            "flexnode",
            "kubelet-proxy",
            apiPolicyFileName);
        string apiPolicy = File.ReadAllText(apiPolicyFilePath);

        // Build the unified config envelope.
        var envelope = new JsonObject
        {
            ["version"] = "1.0",
            ["flexNodeConfig"] = JsonNode.Parse(configJson),
            ["cniConfig"] = new JsonObject
            {
                ["cniVersion"] = "0.3.1",
                ["name"] = "bridge",
                ["type"] = "bridge",
                ["bridge"] = "cni0",
                ["isGateway"] = true,
                ["ipMasq"] = true,
                ["ipam"] = new JsonObject
                {
                    ["type"] = "host-local",
                    ["ranges"] = new JsonArray
                    {
                        new JsonArray
                        {
                            new JsonObject
                            {
                                ["subnet"] = nodeIpInfo.Subnet,
                                ["rangeStart"] =
                                    nodeIpInfo.PodRangeStart,
                                ["rangeEnd"] =
                                    nodeIpInfo.PodRangeEnd,
                                ["gateway"] = nodeIpInfo.Gateway
                            }
                        }
                    },
                    ["routes"] = new JsonArray
                    {
                        new JsonObject { ["dst"] = "0.0.0.0/0" }
                    }
                }
            },
            ["netplan"] = new JsonObject
            {
                ["network"] = new JsonObject
                {
                    ["version"] = 2,
                    ["ethernets"] = new JsonObject
                    {
                        ["eth0"] = new JsonObject
                        {
                            ["addresses"] = new JsonArray
                            {
                                $"{nodeIpInfo.NodeIp}/16"
                            },
                            ["dhcp4"] = true,
                            ["dhcp4-overrides"] = new JsonObject
                            {
                                ["route-metric"] = 100
                            },
                            ["dhcp6"] = false
                        }
                    }
                }
            },
            ["apiServerProxyConfig"] = new JsonObject
            {
                ["proxyListenAddr"] = "127.0.0.1:6444",
                ["insecure"] = insecure,
                ["signingCert"] = signingCertPem
            },
            ["kubeletProxyConfig"] = new JsonObject
            {
                ["apiPolicy"] = JsonNode.Parse(apiPolicy)
            }
        };

        if (gpuConfig != null)
        {
            string gpuJson = JsonSerializer.Serialize(
                gpuConfig, Utils.Options);
            envelope["gpuConfig"] = JsonNode.Parse(gpuJson);
        }

        var jsonOpts =
            new JsonSerializerOptions { WriteIndented = true };
        string envelopeJson = envelope.ToJsonString(jsonOpts);

        return $@"#cloud-config
write_files:
  - path: /etc/cleanroom-boot/cleanroom-config.json
    permissions: ""0644""
    content: |
{Indent8(envelopeJson)}";

        // Indent for YAML embedding (8 spaces).
        static string Indent8(string text)
        {
            return string.Join(
                "\n",
                text.Split('\n').Select(
                    l => string.IsNullOrWhiteSpace(l)
                        ? l : "        " + l));
        }
    }

    private async Task CreateNodeViaImageAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string vmName,
        string location,
        ResourceIdentifier flexNodeSubnetId,
        FlexNodeIpInfo nodeIpInfo,
        UserAssignedIdentityResource kubeletMi,
        FlexNodeProfileInput flexNodeProfile,
        string vmSizeValue,
        string configJson,
        string flexNodeImageId,
        KubectlClient kubectlClient,
        IReadOnlyList<IPTag> ipTags,
        bool forceCreate)
    {
        var vm = await this.CreateVmForImageAsync(
            resourceGroupResource,
            clClusterName,
            vmName,
            location,
            flexNodeSubnetId,
            nodeIpInfo,
            kubeletMi,
            flexNodeProfile.SshPublicKey!,
            vmSizeValue,
            flexNodeProfile.OsDiskSizeInGB,
            configJson,
            flexNodeProfile.PolicySigningCertPem!,
            flexNodeProfile.Insecure,
            flexNodeImageId,
            ipTags,
            forceCreate,
            flexNodeProfile.Gpu);

        // With the baked image, all software is pre-installed. The
        // initialize-cleanroom.service reads config files dropped by
        // cloud-init write_files and configures everything at first
        // boot. Wait for the node to join the cluster, then capture
        // the serial console log for diagnostics.
        try
        {
            this.logger.LogInformation(
                $"Waiting for flex node '{vmName}' to join cluster...");
            await this.WaitForNodeToJoinClusterAsync(
                vmName, kubectlClient);

            this.logger.LogInformation(
                $"Waiting for cleanroom-boot to complete " +
                $"on '{vmName}'...");
            await this.WaitForBootCompleteAsync(
                vmName, kubectlClient);
        }
        finally
        {
            await this.CaptureSerialConsoleLogAsync(
                resourceGroupResource, vmName);
        }

        this.logger.LogInformation(
            $"Configuring flex node '{vmName}' taints and labels...");
        await this.ConfigureNodeTaintAndLabelAsync(
            vmName, vmSizeValue, kubectlClient);

        this.logger.LogInformation(
            $"Flex node VM '{vmName}' deployed and joined cluster.");

        await kubectlClient.LabelNodeAsync(
            vm.Data.Name,
            "cleanroom.azure.com/ready=true",
            overwrite: true);
    }

    private async Task CreateNodeViaSshAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string vmName,
        string location,
        ResourceIdentifier flexNodeSubnetId,
        FlexNodeIpInfo nodeIpInfo,
        UserAssignedIdentityResource kubeletMi,
        FlexNodeProfileInput flexNodeProfile,
        string vmSizeValue,
        string configJson,
        string sshPrivateKeyPath,
        KubectlClient kubectlClient,
        ISshSessionFactory sshSessionFactory,
        IReadOnlyList<IPTag> ipTags,
        bool forceCreate,
        IProgress<string> progressReporter)
    {
        // 1. Create VM with same cloud-init as image path.
        var vm = await CreateVmForSshAsync(
            resourceGroupResource,
            clClusterName,
            vmName,
            location,
            flexNodeSubnetId,
            nodeIpInfo,
            kubeletMi,
            flexNodeProfile.SshPublicKey!,
            vmSizeValue,
            flexNodeProfile.OsDiskSizeInGB,
            configJson,
            flexNodeProfile.PolicySigningCertPem!,
            flexNodeProfile.Insecure,
            flexNodeProfile.Gpu,
            forceCreate);

        // 2. Establish SSH session.
        await using var sshSession =
            await CreateSshSessionWithRetryAsync(
                sshSessionFactory,
                resourceGroupResource,
                vm,
                progressReporter);

        // 3. Install prerequisites via SSH.
        // 3a. Install GPU driver (if needed).
        if (IsGpuVmSize(vmSizeValue))
        {
            await InstallGpuDriverAsync(
                sshSession,
                vm.Data.Name,
                sshPrivateKeyPath,
                progressReporter);
        }

        // 3b. Install flex-node agent software (without starting it).
        await InstallFlexNodeSoftwareAsync(
            sshSession,
            vm.Data.Name,
            sshPrivateKeyPath,
            progressReporter);

        // 3c. Copy api-server-proxy package to VM and run install.sh.
        const string ProxyListenAddr = "127.0.0.1:6444";
        string apiServerProxyPackageDir =
            await PullApiServerProxyPackageAsync(vm.Data.Name);
        await CopyPackageToVmAsync(
            sshSession,
            sshPrivateKeyPath,
            apiServerProxyPackageDir,
            "/opt/api-server-proxy");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            "sudo bash /opt/api-server-proxy/install.sh " +
            "--env aks " +
            "--local-binary /opt/api-server-proxy/api-server-proxy " +
            $"--listen-addr {ProxyListenAddr}");

        // 3d. Copy kubelet-proxy package to VM and run install.sh.
        string kubeletProxyPackageDir =
            await PullKubeletProxyPackageAsync(vm.Data.Name);
        await CopyPackageToVmAsync(
            sshSession,
            sshPrivateKeyPath,
            kubeletProxyPackageDir,
            "/opt/kubelet-proxy");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            "sudo bash /opt/kubelet-proxy/install.sh " +
            "--env aks " +
            "--local-binary /opt/kubelet-proxy/kubelet-proxy");

        // 3e. Install cleanroom-boot binary on VM.
        await InstallCleanroomBootAsync(
            sshSession,
            vm.Data.Name,
            sshPrivateKeyPath,
            progressReporter);

        // 4. Start initialize-cleanroom.service (non-blocking;
        //    survives netplan apply which would kill SSH).
        this.logger.LogInformation(
            $"Starting cleanroom-boot on '{vmName}'...");
        progressReporter.Report(
            $"Starting cleanroom-boot on VM {vmName}...");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            "sudo systemctl start " +
            "initialize-cleanroom --no-block");

        // 5. Wait for cleanroom-boot to complete (boot-complete label).
        this.logger.LogInformation(
            $"Waiting for cleanroom-boot to complete " +
            $"on '{vmName}'...");
        await this.WaitForBootCompleteAsync(vmName, kubectlClient);

        // 6. Configure node taints and labels.
        this.logger.LogInformation(
            $"Configuring flex node '{vmName}' taints and labels...");
        await this.ConfigureNodeTaintAndLabelAsync(
            vmName, vmSizeValue, kubectlClient);

        this.logger.LogInformation(
            $"Flex node VM '{vmName}' deployed and joined cluster.");

        await kubectlClient.LabelNodeAsync(
            vm.Data.Name,
            "cleanroom.azure.com/ready=true",
            overwrite: true);

        async Task<VirtualMachineResource> CreateVmForSshAsync(
            ResourceGroupResource resourceGroupResource,
            string clClusterName,
            string vmName,
            string location,
            ResourceIdentifier flexNodeSubnetId,
            FlexNodeIpInfo nodeIpInfo,
            UserAssignedIdentityResource kubeletMi,
            string sshPublicKey,
            string vmSize,
            int? osDiskSizeInGB,
            string configJson,
            string signingCertPem,
            bool insecure,
            FlexNodeGpuConfigInput? gpuConfig,
            bool forceCreate)
        {
            if (!forceCreate)
            {
                try
                {
                    var existingVm =
                        await resourceGroupResource
                            .GetVirtualMachineAsync(vmName);
                    this.logger.LogInformation(
                        $"Found existing VM so skipping creation:" +
                        $" {vmName}");
                    return existingVm;
                }
                catch (RequestFailedException rfe)
                    when (rfe.Status == (int)HttpStatusCode.NotFound)
                {
                    // Does not exist. Proceed to creation.
                }
            }

            this.logger.LogInformation($"Creating VM: {vmName}");

            var publicIp = await this.CreatePublicIpAsync(
                resourceGroupResource,
                clClusterName,
                vmName + "-ip",
                location,
                forceCreate,
                ipTags);

            var nic = await this.CreateNetworkInterfaceAsync(
                resourceGroupResource,
                clClusterName,
                vmName + "-nic",
                location,
                flexNodeSubnetId,
                nodeIpInfo,
                publicIp,
                forceCreate);

            string cloudInitYaml = GetCloudInitYaml(
                nodeIpInfo,
                configJson,
                signingCertPem,
                insecure,
                gpuConfig);

            string resolvedVmSize = vmSize ?? "Standard_DC2as_v5";
            var collection =
                resourceGroupResource.GetVirtualMachines();
            var vmData = new VirtualMachineData(
                new AzureLocation(location))
            {
                Tags =
                {
                    { FlexNodeTag, clClusterName }
                },
                HardwareProfile = new VirtualMachineHardwareProfile
                {
                    VmSize =
                        new VirtualMachineSizeType(resolvedVmSize)
                },
                StorageProfile = new VirtualMachineStorageProfile
                {
                    ImageReference = new ImageReference
                    {
                        Publisher = "Canonical",
                        Offer = "ubuntu-24_04-lts",
                        Sku = "cvm",
                        Version = "24.04.202607310"
                    },
                    OSDisk = new VirtualMachineOSDisk(
                        DiskCreateOptionType.FromImage)
                    {
                        Name = vmName + "-osdisk",
                        Caching = CachingType.ReadWrite,
                        DiskSizeGB = osDiskSizeInGB ??
                            (IsGpuVmSize(resolvedVmSize)
                                ? 128 : null),
                        ManagedDisk = new VirtualMachineManagedDisk
                        {
                            StorageAccountType =
                                StorageAccountType.StandardLrs,
                            SecurityProfile =
                                new VirtualMachineDiskSecurityProfile
                                {
                                    SecurityEncryptionType =
                                    IsGpuVmSize(resolvedVmSize)
                                    ? SecurityEncryptionType
                                        .DiskWithVmGuestState
                                    : SecurityEncryptionType
                                        .VmGuestStateOnly
                                }
                        }
                    }
                },
                SecurityProfile = new SecurityProfile
                {
                    SecurityType = SecurityType.ConfidentialVm,
                    UefiSettings = new UefiSettings
                    {
                        IsSecureBootEnabled = true,
                        IsVirtualTpmEnabled = true
                    }
                },
                OSProfile = new VirtualMachineOSProfile
                {
                    ComputerName = vmName,
                    AdminUsername = "azureuser",
                    CustomData = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(cloudInitYaml)),
                    LinuxConfiguration = new LinuxConfiguration
                    {
                        DisablePasswordAuthentication = true,
                        SshPublicKeys =
                        {
                            new SshPublicKeyConfiguration
                            {
                                Path = "/home/azureuser" +
                                    "/.ssh/authorized_keys",
                                KeyData = sshPublicKey
                            }
                        }
                    }
                },
                NetworkProfile = new VirtualMachineNetworkProfile
                {
                    NetworkInterfaces =
                    {
                        new VirtualMachineNetworkInterfaceReference
                        {
                            Id = nic.Id,
                            Primary = true
                        }
                    }
                },
                Identity = new ManagedServiceIdentity(
                    ManagedServiceIdentityType
                        .SystemAssignedUserAssigned)
                {
                    UserAssignedIdentities =
                    {
                        {
                            kubeletMi.Id,
                            new UserAssignedIdentity()
                        }
                    }
                },
                BootDiagnostics = new BootDiagnostics
                {
                    Enabled = true
                }
            };

            var vm = (await collection.CreateOrUpdateAsync(
                WaitUntil.Completed, vmName, vmData)).Value;
            this.logger.LogInformation($"VM created: {vm.Id}");

            return vm;
        }

        async Task<ISshSession> CreateSshSessionWithRetryAsync(
            ISshSessionFactory sshSessionFactory,
            ResourceGroupResource resourceGroupResource,
            VirtualMachineResource vm,
            IProgress<string> progressReporter)
        {
            var maxWait = TimeSpan.FromMinutes(10);
            var retryInterval = TimeSpan.FromSeconds(15);
            var elapsed = TimeSpan.Zero;
            string vmName = vm.Data.Name;

            this.logger.LogInformation(
                $"Creating SSH session to VM {vmName} " +
                $"(timeout: {maxWait.TotalMinutes} minutes)...");
            progressReporter.Report(
                $"Creating SSH session to VM {vmName}...");

            while (elapsed < maxWait)
            {
                ISshSession? session = null;
                try
                {
                    session =
                        await sshSessionFactory.CreateSessionAsync(
                            resourceGroupResource, vm);

                    this.logger.LogInformation(
                        $"SSH session established to VM " +
                        $"{vmName}.");
                    return session;
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(
                        $"Failed to create SSH session: " +
                        $"{ex.Message}");
                    if (session != null)
                    {
                        await session.DisposeAsync();
                    }
                }

                this.logger.LogInformation(
                    $"SSH not yet available for VM {vmName}" +
                    $", retrying in " +
                    $"{retryInterval.TotalSeconds} seconds..." +
                    $" ({elapsed.TotalSeconds}s elapsed)");
                await Task.Delay(retryInterval);
                elapsed += retryInterval;
            }

            throw new TimeoutException(
                $"SSH session to VM {vmName} was not established " +
                $"within {maxWait.TotalMinutes} minutes.");
        }

        // GPU driver is installed manually (not via GPU Operator)
        // because CC mode requires LKCA config and initramfs rebuild
        // before the driver install — an order that GPU Operator's
        // driver container does not support.
        async Task InstallGpuDriverAsync(
            ISshSession sshSession,
            string vmName,
            string sshPrivateKeyPath,
            IProgress<string> progressReporter)
        {
            this.logger.LogInformation(
                $"Installing GPU driver on flex node VM " +
                $"'{vmName}'...");
            progressReporter.Report(
                $"Installing GPU driver on VM {vmName}...");

            string scriptPath = Path.Combine(
                AppContext.BaseDirectory,
                "flexnode",
                "install-gpu-driver.sh");
            string script =
                await File.ReadAllTextAsync(scriptPath);

            await ExecuteScriptViaSshAsync(
                sshSession, script, vmName, sshPrivateKeyPath);

            this.logger.LogInformation(
                $"GPU driver installed on flex node VM " +
                $"'{vmName}'.");
            progressReporter.Report(
                $"GPU driver installed on VM {vmName}.");
        }

        async Task InstallFlexNodeSoftwareAsync(
            ISshSession sshSession,
            string vmName,
            string sshPrivateKeyPath,
            IProgress<string> progressReporter)
        {
            this.logger.LogInformation(
                $"Installing flex-node agent software on " +
                $"VM: {vmName}");
            progressReporter.Report(
                $"Installing flex-node agent software on " +
                $"VM {vmName}...");

            const string AksFlexNodeVersion = "v0.0.19";
#pragma warning disable MEN002 // Line is too long
            string script = $@"#!/bin/bash
set -e

AKS_FLEX_NODE_VERSION=""{AksFlexNodeVersion}""

# Cleanup previous aks-flex-node setup if any.
echo ""Running aks-flex-node uninstall script to cleanup previous setup...""
curl -fsSL https://raw.githubusercontent.com/Azure/AKSFlexNode/refs/tags/${{AKS_FLEX_NODE_VERSION}}/scripts/uninstall.sh -o /tmp/aks-flex-node-uninstall.sh
sed -i 's/^\([[:space:]]*\)remove_azure_cli$/\1#remove_azure_cli/' /tmp/aks-flex-node-uninstall.sh
sudo bash /tmp/aks-flex-node-uninstall.sh --force || true

# Run the AKS Flex Node install script (installs containerd, kubelet, systemd units).
echo ""Running AKS Flex Node install script...""
curl -fsSL https://raw.githubusercontent.com/Azure/AKSFlexNode/refs/tags/${{AKS_FLEX_NODE_VERSION}}/scripts/install.sh -o /tmp/aks-flex-node-install.sh
sed -i ""s/version=\$(get_latest_release)/version=\""${{AKS_FLEX_NODE_VERSION}}\""/"" /tmp/aks-flex-node-install.sh
sed -i 's/^\([[:space:]]*\)install_azure_cli$/\1#install_azure_cli/' /tmp/aks-flex-node-install.sh
sed -i 's/^\([[:space:]]*\)check_azure_cli_auth$/\1#check_azure_cli_auth/' /tmp/aks-flex-node-install.sh
sed -i 's/^\([[:space:]]*\)setup_permissions$/\1#setup_permissions/' /tmp/aks-flex-node-install.sh
sudo bash /tmp/aks-flex-node-install.sh

echo ""Flex-node agent software installed successfully.""
";
#pragma warning restore MEN002 // Line is too long

            await ExecuteScriptViaSshAsync(
                sshSession, script, vmName, sshPrivateKeyPath);

            this.logger.LogInformation(
                $"Flex-node agent software installed on " +
                $"VM: {vmName}");
        }

        async Task CopyPackageToVmAsync(
            ISshSession sshSession,
            string sshPrivateKeyPath,
            string localPackageDir,
            string remoteDir)
        {
            this.logger.LogInformation(
                $"Copying package from {localPackageDir} " +
                $"to {remoteDir}...");

            await sshSession.RunCommandAsync(
                "azureuser",
                sshPrivateKeyPath,
                $"sudo mkdir -p {remoteDir} && " +
                $"sudo chown azureuser:azureuser {remoteDir}");

            // Copy all files recursively so that subdirectories (e.g. aks/ and
            // kind/ holding the environment-specific configure.sh scripts) are
            // preserved on the remote.
            foreach (var file in Directory.GetFiles(
                localPackageDir, "*", SearchOption.AllDirectories))
            {
                string relativePath =
                    Path.GetRelativePath(localPackageDir, file)
                        .Replace(Path.DirectorySeparatorChar, '/');
                string remotePath = $"{remoteDir}/{relativePath}";
                string remoteFileDir = Path.GetDirectoryName(remotePath)!
                    .Replace(Path.DirectorySeparatorChar, '/');

                byte[] fileBytes =
                    await File.ReadAllBytesAsync(file);
                string base64Content =
                    Convert.ToBase64String(fileBytes);

                await sshSession.RunCommandAsync(
                    "azureuser",
                    sshPrivateKeyPath,
                    $"mkdir -p {remoteFileDir} && " +
                    $"base64 -d > {remotePath} && " +
                    $"chmod +x {remotePath}",
                    base64Content);

                this.logger.LogInformation(
                    $"  Copied {relativePath} -> " +
                    $"{remotePath}");
            }
        }

        async Task InstallCleanroomBootAsync(
            ISshSession sshSession,
            string vmName,
            string sshPrivateKeyPath,
            IProgress<string> progressReporter)
        {
            this.logger.LogInformation(
                $"Installing cleanroom-boot on " +
                $"VM: {vmName}");
            progressReporter.Report(
                $"Installing cleanroom-boot on VM {vmName}...");

            // Pull the cleanroom-boot binary from OCI registry
            // and copy it to /usr/local/bin on the VM.
            string packageUrl =
                ImageUtils.CleanroomBootPackageUrl();
            var oras = new OrasClient(
                this.logger, this.configuration);
            string packageDir = Path.Combine(
                Path.GetTempPath(),
                $"{vmName}-cleanroom-boot-pkg");
            if (Directory.Exists(packageDir))
            {
                Directory.Delete(packageDir, recursive: true);
            }

            Directory.CreateDirectory(packageDir);
            await oras.Pull(packageUrl, packageDir);

            await CopyPackageToVmAsync(
                sshSession,
                sshPrivateKeyPath,
                packageDir,
                "/usr/local/bin");

            // Install the initialize-cleanroom.service systemd
            // unit. This matches the service baked by image-prep
            // so both paths use the identical unit definition.
            const string ServiceUnit = """
                [Unit]
                Description=Cleanroom Flex Node Boot Configuration

                [Service]
                Type=oneshot
                RemainAfterExit=yes
                ExecStartPre=/bin/bash -c '\
                  config="/etc/cleanroom-boot/cleanroom-config.json"; \
                  echo "Waiting for $config ..."; \
                  for i in $(seq 1 20); do \
                    [ -f "$config" ] && echo "Found $config" && exit 0; \
                    sleep 15; \
                  done; \
                  echo "ERROR: $config not found after 300s."; \
                  echo "Contents of /etc/cleanroom-boot:"; \
                  ls -la /etc/cleanroom-boot/ 2>&1 || echo "Directory does not exist"; \
                  exit 1'
                ExecStart=cleanroom-boot \
                  --config /etc/cleanroom-boot/cleanroom-config.json \
                  --boot-dir /etc/cleanroom-boot \
                  --api-server-proxy-dir /opt/api-server-proxy \
                  --kubelet-proxy-dir /opt/kubelet-proxy
                ExecStart=/usr/bin/systemctl disable initialize-cleanroom.service
                StandardOutput=journal+console
                StandardError=journal+console
                TimeoutStartSec=900

                [Install]
                WantedBy=multi-user.target
                """;

            await sshSession.RunCommandAsync(
                "azureuser",
                sshPrivateKeyPath,
                "cat | sudo tee " +
                "/etc/systemd/system/" +
                "initialize-cleanroom.service > /dev/null",
                ServiceUnit);

            await sshSession.RunCommandAsync(
                "azureuser",
                sshPrivateKeyPath,
                "sudo systemctl daemon-reload");

            this.logger.LogInformation(
                $"cleanroom-boot and " +
                $"initialize-cleanroom.service installed " +
                $"on VM: {vmName}");
        }

        async Task<string> PullApiServerProxyPackageAsync(
            string vmName)
        {
            string packageUrl =
                ImageUtils.ApiServerProxyPackageUrl();
            this.logger.LogInformation(
                $"Pulling api-server-proxy package from " +
                $"{packageUrl}...");
            var oras = new OrasClient(
                this.logger, this.configuration);
            string packageDir = Path.Combine(
                Path.GetTempPath(),
                $"{vmName}-api-server-proxy-pkg");
            if (Directory.Exists(packageDir))
            {
                Directory.Delete(packageDir, recursive: true);
            }

            Directory.CreateDirectory(packageDir);
            await oras.Pull(packageUrl, packageDir);
            this.logger.LogInformation(
                "api-server-proxy package pulled " +
                "successfully.");
            return packageDir;
        }

        async Task<string> PullKubeletProxyPackageAsync(
            string vmName)
        {
            string packageUrl =
                ImageUtils.KubeletProxyPackageUrl();
            this.logger.LogInformation(
                $"Pulling kubelet-proxy package from " +
                $"{packageUrl}...");
            var oras = new OrasClient(
                this.logger, this.configuration);
            string packageDir = Path.Combine(
                Path.GetTempPath(),
                $"{vmName}-kubelet-proxy-pkg");
            if (Directory.Exists(packageDir))
            {
                Directory.Delete(packageDir, recursive: true);
            }

            Directory.CreateDirectory(packageDir);
            await oras.Pull(packageUrl, packageDir);
            this.logger.LogInformation(
                "kubelet-proxy package pulled successfully.");
            return packageDir;
        }

        async Task ExecuteScriptViaSshAsync(
            ISshSession sshSession,
            string script,
            string vmName,
            string privateKeyPath)
        {
            var remoteScriptPath =
                $"/tmp/install-flex-node-agent-" +
                $"{Guid.NewGuid():N}.sh";

            try
            {
                this.logger.LogInformation(
                    $"Executing script via SSH proxy on " +
                    $"VM {vmName}...");

                // Copy the script to the remote VM.
                await sshSession.RunCommandAsync(
                    "azureuser",
                    privateKeyPath,
                    $"cat > {remoteScriptPath}",
                    script);

                this.logger.LogInformation(
                    $"Script uploaded to {remoteScriptPath}" +
                    $" on VM {vmName}.");

                // Make the script executable and run it.
                var command =
                    $"chmod +x {remoteScriptPath} && " +
                    $"{remoteScriptPath}";
                this.logger.LogInformation(
                    $"Executing installation script on " +
                    $"VM {vmName}...");

                await sshSession.RunCommandAsync(
                    "azureuser", privateKeyPath, command);

                this.logger.LogInformation(
                    $"Installation script completed " +
                    $"successfully on VM {vmName}.");

                // Clean up the remote script file.
                try
                {
                    await sshSession.RunCommandAsync(
                        "azureuser",
                        privateKeyPath,
                        $"rm -f {remoteScriptPath}");
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(
                        $"Failed to clean up remote script " +
                        $"file {remoteScriptPath} on VM " +
                        $"{vmName}: {ex.Message}");
                }

                return;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    $"Script execution via SSH failed on " +
                    $"VM {vmName}: {ex.Message}.");
                throw;
            }
        }
    }

    private async Task<VirtualMachineResource> CreateVmForImageAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string vmName,
        string location,
        ResourceIdentifier flexNodeSubnetId,
        FlexNodeIpInfo nodeIpInfo,
        UserAssignedIdentityResource kubeletMi,
        string sshPublicKey,
        string vmSize,
        int? osDiskSizeInGB,
        string configJson,
        string signingCertPem,
        bool insecure,
        string flexNodeImageId,
        IReadOnlyList<IPTag> ipTags,
        bool forceCreate,
        FlexNodeGpuConfigInput? gpuConfig = null)
    {
        if (!forceCreate)
        {
            try
            {
                var existingVm =
                    await resourceGroupResource
                        .GetVirtualMachineAsync(vmName);
                this.logger.LogInformation(
                    $"Found existing VM so skipping creation:" +
                    $" {vmName}");
                return existingVm;
            }
            catch (RequestFailedException rfe)
                when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                // Does not exist. Proceed to creation.
            }
        }

        this.logger.LogInformation($"Creating VM: {vmName}");

        var publicIp = await this.CreatePublicIpAsync(
            resourceGroupResource,
            clClusterName,
            vmName + "-ip",
            location,
            forceCreate,
            ipTags);

        var nic = await this.CreateNetworkInterfaceAsync(
            resourceGroupResource,
            clClusterName,
            vmName + "-nic",
            location,
            flexNodeSubnetId,
            nodeIpInfo,
            publicIp,
            forceCreate);

        string cloudInitYaml = GetCloudInitYaml(
            nodeIpInfo,
            configJson,
            signingCertPem,
            insecure,
            gpuConfig);

        var collection =
            resourceGroupResource.GetVirtualMachines();
        var vmData = new VirtualMachineData(
            new AzureLocation(location))
        {
            Tags =
            {
                { FlexNodeTag, clClusterName }
            },
            HardwareProfile = new VirtualMachineHardwareProfile
            {
                VmSize = new VirtualMachineSizeType(vmSize)
            },
            StorageProfile = new VirtualMachineStorageProfile
            {
                ImageReference = new ImageReference
                {
                    // Baked gallery image resolved from the
                    // digests document.
                    Id = new ResourceIdentifier(flexNodeImageId)
                },
                OSDisk = new VirtualMachineOSDisk(
                    DiskCreateOptionType.FromImage)
                {
                    Name = vmName + "-osdisk",
                    Caching = CachingType.ReadWrite,
                    DiskSizeGB = osDiskSizeInGB ??
                        (IsGpuVmSize(vmSize) ? 128 : null),
                    ManagedDisk = new VirtualMachineManagedDisk
                    {
                        StorageAccountType =
                            StorageAccountType.StandardLrs,
                        SecurityProfile =
                            new VirtualMachineDiskSecurityProfile
                            {
                                SecurityEncryptionType =
                                SecurityEncryptionType
                                    .VmGuestStateOnly
                            }
                    }
                }
            },
            SecurityProfile = new SecurityProfile
            {
                SecurityType = SecurityType.ConfidentialVm,
                UefiSettings = new UefiSettings
                {
                    IsSecureBootEnabled = true,
                    IsVirtualTpmEnabled = true
                }
            },
            OSProfile = new VirtualMachineOSProfile
            {
                ComputerName = vmName,
                AdminUsername = "azureuser",
                CustomData = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(cloudInitYaml)),
                LinuxConfiguration = new LinuxConfiguration
                {
                    DisablePasswordAuthentication = true,
                    SshPublicKeys =
                    {
                        new SshPublicKeyConfiguration
                        {
                            Path = "/home/azureuser" +
                                "/.ssh/authorized_keys",
                            KeyData = sshPublicKey
                        }
                    }
                }
            },
            NetworkProfile = new VirtualMachineNetworkProfile
            {
                NetworkInterfaces =
                {
                    new VirtualMachineNetworkInterfaceReference
                    {
                        Id = nic.Id,
                        Primary = true
                    }
                }
            },
            Identity = new ManagedServiceIdentity(
                ManagedServiceIdentityType
                    .SystemAssignedUserAssigned)
            {
                UserAssignedIdentities =
                {
                    {
                        kubeletMi.Id,
                        new UserAssignedIdentity()
                    }
                }
            },
            BootDiagnostics = new BootDiagnostics
            {
                Enabled = true
            }
        };

        var vm = (await collection.CreateOrUpdateAsync(
            WaitUntil.Completed, vmName, vmData)).Value;
        this.logger.LogInformation($"VM created: {vm.Id}");

        return vm;
    }

    private async Task WaitForBootCompleteAsync(
        string vmName,
        KubectlClient kubectlClient)
    {
        this.logger.LogInformation(
            $"Waiting for cleanroom-boot to complete " +
            $"on '{vmName}'...");

        var maxWait = TimeSpan.FromMinutes(15);
        var waitInterval = TimeSpan.FromSeconds(15);
        var elapsed = TimeSpan.Zero;

        while (elapsed < maxWait)
        {
            if (await kubectlClient.NodeHasLabelAsync(
                vmName,
                "cleanroom.azure.com/boot-complete=true"))
            {
                this.logger.LogInformation(
                    $"cleanroom-boot completed on '{vmName}'.");
                return;
            }

            await Task.Delay(waitInterval);
            elapsed += waitInterval;
            this.logger.LogInformation(
                $"Still waiting for cleanroom-boot " +
                $"on '{vmName}'... " +
                $"({elapsed.TotalSeconds}s elapsed)");
        }

        throw new TimeoutException(
            $"cleanroom-boot on '{vmName}' did not complete " +
            $"within {maxWait.TotalMinutes} minute(s).");
    }

    private async Task CaptureSerialConsoleLogAsync(
        ResourceGroupResource resourceGroupResource,
        string vmName)
    {
        try
        {
            this.logger.LogInformation(
                $"Capturing serial console log for " +
                $"'{vmName}'...");

            var vmResource =
                await resourceGroupResource
                    .GetVirtualMachineAsync(vmName);

            var diagResult =
                await vmResource.Value
                    .RetrieveBootDiagnosticsDataAsync(
                        sasUriExpirationTimeInMinutes: 5);

            var serialConsoleUri =
                diagResult.Value.SerialConsoleLogBlobUri;

            if (serialConsoleUri == null)
            {
                this.logger.LogWarning(
                    $"No serial console log available " +
                    $"for '{vmName}'.");
                return;
            }

            string consoleLog;
            using (var httpClient = new HttpClient())
            {
                consoleLog = await httpClient
                    .GetStringAsync(serialConsoleUri);
            }

            // Log the full serial console output.
            foreach (string line in consoleLog.Split('\n'))
            {
                this.logger.LogInformation(
                    $"  [{vmName}] {line.TrimEnd()}");
            }
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                $"Could not capture serial console log " +
                $"for '{vmName}': {ex.Message}");
        }
    }

    private string GenerateFlexNodeConfig(
        string subscriptionId,
        string tenantId,
        string kubeletMiClientId,
        string aksResourceId,
        string location,
        int maxPodsPerNode,
        string k8sVersion)
    {
        var config = new
        {
            azure = new
            {
                subscriptionId,
                tenantId,
                cloud = "AzurePublicCloud",
                managedIdentity = new
                {
                    clientId = kubeletMiClientId
                },
                targetCluster = new
                {
                    resourceId = aksResourceId,
                    location
                }
            },
            kubernetes = new
            {
                version = k8sVersion
            },
            node = new
            {
                kubelet = new
                {
                    dnsServiceIp = "168.63.129.16"
                },
                maxPods = maxPodsPerNode
            },
            agent = new
            {
                logLevel = "debug",
                logDir = "/var/log/aks-flex-node"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<UserAssignedIdentityResource> CreateManagedIdentityAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string location,
        string miName,
        bool forceCreate)
    {
        if (!forceCreate)
        {
            try
            {
                var existingMi = await resourceGroupResource.GetUserAssignedIdentityAsync(miName);
                this.logger.LogInformation(
                    $"Found existing managed identity so skipping creation: {miName}");
                return existingMi;
            }
            catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                // Does not exist. Proceed to creation.
            }
        }

        this.logger.LogInformation($"Creating managed identity: {miName}");
        var collection = resourceGroupResource.GetUserAssignedIdentities();
        var data = new UserAssignedIdentityData(new AzureLocation(location))
        {
            Tags =
            {
                { FlexNodeTag, clClusterName }
            }
        };

        var mi = (await collection.CreateOrUpdateAsync(WaitUntil.Completed, miName, data)).Value;
        this.logger.LogInformation($"Managed identity created: {mi.Id}");
        return mi;
    }

    private async Task AssignOwnerRoleToMiAsync(
        ContainerServiceManagedClusterResource aksResource,
        UserAssignedIdentityResource mi)
    {
        string subscriptionId = aksResource.Id.SubscriptionId!;
        string ownerRoleDefinitionId = $"/subscriptions/{subscriptionId}/providers/" +
            $"Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635";
        string roleAssignmentId = Guid.NewGuid().ToString();

        var roleAssignmentData = new RoleAssignmentCreateOrUpdateContent(
            new ResourceIdentifier(ownerRoleDefinitionId),
            mi.Data.PrincipalId!.Value)
        {
            PrincipalType = "ServicePrincipal",
        };

        var collection = aksResource.GetRoleAssignments();
        try
        {
            await collection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                roleAssignmentId,
                roleAssignmentData);
            this.logger.LogInformation(
                $"Owner role assigned to MI {mi.Data.Name} on AKS cluster.");
        }
        catch (RequestFailedException rfe) when (rfe.ErrorCode == "RoleAssignmentExists")
        {
            this.logger.LogInformation("Owner role assignment already exists.");
        }
    }

    private async Task SetupKubernetesRbacAsync(
        UserAssignedIdentityResource kubeletMi,
        KubectlClient kubectlClient)
    {
        string principalId = kubeletMi.Data.PrincipalId!.Value.ToString();

        // Create ClusterRoleBinding for system:node-bootstrapper.
        this.logger.LogInformation("Creating node bootstrapper ClusterRoleBinding...");
        string bootstrapperYaml =
            "apiVersion: rbac.authorization.k8s.io/v1\n" +
            "kind: ClusterRoleBinding\n" +
            "metadata:\n" +
            "  name: aks-flex-node-bootstrapper\n" +
            "roleRef:\n" +
            "  apiGroup: rbac.authorization.k8s.io\n" +
            "  kind: ClusterRole\n" +
            "  name: system:node-bootstrapper\n" +
            "subjects:\n" +
            "- apiGroup: rbac.authorization.k8s.io\n" +
            "  kind: User\n" +
            $"  name: {principalId}\n";

        var bootstrapperFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(bootstrapperFile, bootstrapperYaml);
        await kubectlClient.ApplyAsync(bootstrapperFile);

        // Create ClusterRoleBinding for system:node.
        this.logger.LogInformation("Creating node ClusterRoleBinding...");
        string nodeRoleYaml =
            "apiVersion: rbac.authorization.k8s.io/v1\n" +
            "kind: ClusterRoleBinding\n" +
            "metadata:\n" +
            "  name: aks-flex-node-role\n" +
            "roleRef:\n" +
            "  apiGroup: rbac.authorization.k8s.io\n" +
            "  kind: ClusterRole\n" +
            "  name: system:node\n" +
            "subjects:\n" +
            "- apiGroup: rbac.authorization.k8s.io\n" +
            "  kind: User\n" +
            $"  name: {principalId}\n";

        var nodeRoleFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(nodeRoleFile, nodeRoleYaml);
        await kubectlClient.ApplyAsync(nodeRoleFile);

        this.logger.LogInformation(
            "Kubernetes RBAC roles configured for kubelet identity.");
    }

    private async Task<PublicIPAddressResource> CreatePublicIpAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string ipName,
        string location,
        bool forceCreate,
        IReadOnlyList<IPTag> ipTags)
    {
        if (!forceCreate)
        {
            try
            {
                var existingIp = await resourceGroupResource.GetPublicIPAddressAsync(ipName);
                this.logger.LogInformation(
                    $"Found existing public IP so skipping creation: {ipName}");
                return existingIp;
            }
            catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                // Does not exist. Proceed to creation.
            }
        }

        this.logger.LogInformation($"Creating public IP: {ipName}");
        var collection = resourceGroupResource.GetPublicIPAddresses();
        var data = new PublicIPAddressData
        {
            Location = new AzureLocation(location),
            Tags =
            {
                { FlexNodeTag, clClusterName }
            },
            PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
            PublicIPAddressVersion = NetworkIPVersion.IPv4,
            Sku = new PublicIPAddressSku
            {
                Name = PublicIPAddressSkuName.Standard
            }
        };

        // Apply the configured service tag (ipTag) to the IP at creation time. A service tag
        // can only be set when the IP is created; an empty ipTags list leaves the IP untagged.
        foreach (var ipTag in ipTags)
        {
            data.IPTags.Add(ipTag);
            this.logger.LogInformation(
                $"Applying ipTag {ipTag.IPTagType}={ipTag.Tag} to public IP: {ipName}");
        }

        var ip = (await collection.CreateOrUpdateAsync(WaitUntil.Completed, ipName, data)).Value;
        this.logger.LogInformation($"Public IP created: {ip.Id}");
        return ip;
    }

    private async Task<NetworkInterfaceResource> CreateNetworkInterfaceAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string nicName,
        string location,
        ResourceIdentifier flexNodeSubnetId,
        FlexNodeIpInfo nodeIpInfo,
        PublicIPAddressResource publicIp,
        bool forceCreate)
    {
        if (!forceCreate)
        {
            try
            {
                var existingNic = await resourceGroupResource.GetNetworkInterfaceAsync(nicName);
                this.logger.LogInformation(
                    $"Found existing NIC so skipping creation: {nicName}");
                return existingNic;
            }
            catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                // Does not exist. Proceed to creation.
            }
        }

        this.logger.LogInformation($"Creating NIC: {nicName}");
        var collection = resourceGroupResource.GetNetworkInterfaces();
        var data = new NetworkInterfaceData
        {
            Location = new AzureLocation(location),
            Tags =
            {
                { FlexNodeTag, clClusterName }
            },
            IPConfigurations =
            {
                new NetworkInterfaceIPConfigurationData
                {
                    Name = "node-ipconfig",
                    PrivateIPAllocationMethod = NetworkIPAllocationMethod.Static,
                    PrivateIPAddress = nodeIpInfo.NodeIp,
                    Subnet = new SubnetData { Id = flexNodeSubnetId },
                    PublicIPAddress = new PublicIPAddressData { Id = publicIp.Id },
                    Primary = true
                }
            }
        };

        // Secondary static IPs for pod networking.
        for (int i = 0; i < nodeIpInfo.TotalPodIps; i++)
        {
            data.IPConfigurations.Add(new NetworkInterfaceIPConfigurationData
            {
                Name = $"pod-ipconfig-{i + 1}",
                PrivateIPAllocationMethod = NetworkIPAllocationMethod.Static,
                PrivateIPAddress = nodeIpInfo.PodIp(i),
                Subnet = new SubnetData { Id = flexNodeSubnetId },
                Primary = false
            });
        }

        var nic = (await collection.CreateOrUpdateAsync(WaitUntil.Completed, nicName, data)).Value;
        this.logger.LogInformation($"NIC created: {nic.Id}");
        return nic;
    }

    private async Task WaitForNodeToJoinClusterAsync(string vmName, KubectlClient kubectlClient)
    {
        var maxWait = TimeSpan.FromMinutes(15);
        var waitInterval = TimeSpan.FromSeconds(30);
        var elapsed = TimeSpan.Zero;

        while (elapsed < maxWait)
        {
            if (await kubectlClient.NodeExistsAsync(vmName))
            {
                this.logger.LogInformation($"Node '{vmName}' has joined the cluster.");
                return;
            }

            await Task.Delay(waitInterval);
            elapsed += waitInterval;
            this.logger.LogInformation(
                $"Still waiting for node '{vmName}' to join... ({elapsed.TotalSeconds}s elapsed)");
        }

        throw new TimeoutException(
            $"Node '{vmName}' did not join the cluster within {maxWait.TotalMinutes} minute(s).");
    }

    private async Task ConfigureNodeTaintAndLabelAsync(
        string vmName,
        string vmSize,
        KubectlClient kubectlClient)
    {
        this.logger.LogInformation($"Adding taint and label to node '{vmName}'...");

        await kubectlClient.TaintNodeAsync(
            vmName,
            "pod-policy=required:NoSchedule",
            overwrite: true);

        await kubectlClient.LabelNodeAsync(
            vmName,
            "cleanroom.azure.com/flexnode=true",
            overwrite: true);

        await kubectlClient.LabelNodeAsync(
            vmName,
            "pod-policy=required",
            overwrite: true);

        await kubectlClient.LabelNodeAsync(
            vmName,
            $"node.kubernetes.io/instance-type={vmSize}",
            overwrite: true);

        this.logger.LogInformation(
            $"Node '{vmName}' configured with taint and label for pod-policy.");
    }
}