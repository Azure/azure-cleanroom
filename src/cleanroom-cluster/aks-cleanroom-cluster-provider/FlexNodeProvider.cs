// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
        ResourceIdentifier flexNodeSubnetId,
        FlexNodeProfileInput flexNodeProfile,
        KubectlClient kubectlClient,
        ISshSessionFactory sshSessionFactory,
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

        var createTasks = new List<Task>();
        for (int i = 0; i < flexNodeProfile.NodeCount; i++)
        {
            int ordinal = i;
            createTasks.Add(Task.Run(async () =>
            {
                string vmName = clClusterName + FlexVmPrefix + ordinal;

                if (await kubectlClient.IsFlexNodeReadyAsync(vmName))
                {
                    this.logger.LogInformation(
                        $"Flex node '{vmName}' already has ready label. Skipping creation.");
                    progressReporter.Report(
                        $"Flex node setup already completed on VM {vmName}...");
                    return;
                }

                this.logger.LogInformation($"Creating flex node VM '{vmName}'...");
                var vm = await this.CreateVmAsync(
                    resourceGroupResource,
                    clClusterName,
                    vmName,
                    location,
                    flexNodeSubnetId,
                    kubeletMi,
                    flexNodeProfile.SshPublicKey!,
                    flexNodeProfile.VmSize,
                    forceCreate);

                await using var sshSession = await this.CreateSshSessionWithRetryAsync(
                    sshSessionFactory,
                    resourceGroupResource,
                    vm,
                    progressReporter);

                // Pull the api-server-proxy OCI package once for both uninstall and install.
                string apiServerProxyPackageDir = await this.PullApiServerProxyPackageAsync(
                    vm.Data.Name);

                await this.UninstallApiServerProxyAsync(
                    sshSession,
                    vm.Data.Name,
                    sshPrivateKeyPath,
                    apiServerProxyPackageDir,
                    progressReporter);

                await this.InstallFlexNodeAgentAsync(
                    sshSession,
                    vm.Data.Name,
                    kubeletMi.Data.ClientId!.Value.ToString(),
                    configJson,
                    sshPrivateKeyPath,
                    progressReporter);

                this.logger.LogInformation(
                    $"Waiting for flex node '{vmName}' to join cluster...");
                await this.WaitForNodeToJoinClusterAsync(vmName, kubectlClient);

                this.logger.LogInformation(
                    $"Configuring flex node '{vmName}' taints and labels...");
                string vmSize = vm.Data.HardwareProfile.VmSize.ToString()!;
                await this.ConfigureNodeTaintAndLabelAsync(vmName, vmSize, kubectlClient);

                await this.InstallApiServerProxyAsync(
                    sshSession,
                    vm.Data.Name,
                    sshPrivateKeyPath,
                    flexNodeProfile.PolicySigningCertPem!,
                    apiServerProxyPackageDir,
                    progressReporter);

                this.logger.LogInformation("Waiting for flex node to join cluster after " +
                    "installing api-server-proxy...");
                await this.WaitForNodeToJoinClusterAsync(vmName, kubectlClient);

                this.logger.LogInformation(
                    $"Flex node VM '{vmName}' deployed and joined cluster.");

                await kubectlClient.LabelNodeAsync(
                    vm.Data.Name,
                    "cleanroom.azure.com/ready=true",
                    overwrite: true);
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

    private async Task<VirtualMachineResource> CreateVmAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string vmName,
        string location,
        ResourceIdentifier flexNodeSubnetId,
        UserAssignedIdentityResource kubeletMi,
        string sshPublicKey,
        string? vmSize,
        bool forceCreate)
    {
        if (!forceCreate)
        {
            try
            {
                var existingVm = await resourceGroupResource.GetVirtualMachineAsync(vmName);
                this.logger.LogInformation(
                    $"Found existing VM so skipping creation: {vmName}");
                return existingVm;
            }
            catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
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
            forceCreate);

        var nic = await this.CreateNetworkInterfaceAsync(
            resourceGroupResource,
            clClusterName,
            vmName + "-nic",
            location,
            flexNodeSubnetId,
            publicIp,
            forceCreate);

        var collection = resourceGroupResource.GetVirtualMachines();
        var vmData = new VirtualMachineData(new AzureLocation(location))
        {
            Tags =
            {
                { FlexNodeTag, clClusterName }
            },
            HardwareProfile = new VirtualMachineHardwareProfile
            {
                VmSize = new VirtualMachineSizeType(vmSize ?? "Standard_DC4as_v5")
            },
            StorageProfile = new VirtualMachineStorageProfile
            {
                ImageReference = new ImageReference
                {
                    Publisher = "Canonical",
                    Offer = "0001-com-ubuntu-confidential-vm-jammy",
                    Sku = "22_04-lts-cvm",
                    Version = "22.04.202601280"
                },
                OSDisk = new VirtualMachineOSDisk(DiskCreateOptionType.FromImage)
                {
                    Name = vmName + "-osdisk",
                    Caching = CachingType.ReadWrite,
                    ManagedDisk = new VirtualMachineManagedDisk
                    {
                        StorageAccountType = StorageAccountType.StandardLrs,
                        SecurityProfile = new VirtualMachineDiskSecurityProfile
                        {
                            SecurityEncryptionType =
                                SecurityEncryptionType.VmGuestStateOnly
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
                LinuxConfiguration = new LinuxConfiguration
                {
                    DisablePasswordAuthentication = true,
                    SshPublicKeys =
                    {
                        new SshPublicKeyConfiguration
                        {
                            Path = "/home/azureuser/.ssh/authorized_keys",
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
                ManagedServiceIdentityType.SystemAssignedUserAssigned)
            {
                UserAssignedIdentities =
                {
                    { kubeletMi.Id, new UserAssignedIdentity() }
                }
            }
        };

        var vm = (await collection.CreateOrUpdateAsync(WaitUntil.Completed, vmName, vmData)).Value;
        this.logger.LogInformation($"VM created: {vm.Id}");

        return vm;
    }

    private async Task<PublicIPAddressResource> CreatePublicIpAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string ipName,
        string location,
        bool forceCreate)
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
                    Name = "ipconfig1",
                    PrivateIPAllocationMethod = NetworkIPAllocationMethod.Dynamic,
                    Subnet = new SubnetData { Id = flexNodeSubnetId },
                    PublicIPAddress = new PublicIPAddressData { Id = publicIp.Id }
                }
            }
        };

        var nic = (await collection.CreateOrUpdateAsync(WaitUntil.Completed, nicName, data)).Value;
        this.logger.LogInformation($"NIC created: {nic.Id}");
        return nic;
    }

    private string GenerateFlexNodeConfig(
        string subscriptionId,
        string tenantId,
        string kubeletMiClientId,
        string aksResourceId,
        string location,
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
            agent = new
            {
                logLevel = "debug",
                logDir = "/var/log/aks-flex-node"
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task InstallFlexNodeAgentAsync(
        ISshSession sshSession,
        string vmName,
        string kubeletMiClientId,
        string configJson,
        string sshPrivateKeyPath,
        IProgress<string> progressReporter)
    {
        this.logger.LogInformation($"Running flex node setup script on VM: {vmName}");
        progressReporter.Report($"Running flex node setup script on VM {vmName}...");
        string scriptTemplatePath = Path.Combine(
            AppContext.BaseDirectory,
            "flexnode",
            "install-flex-node-agent.sh");
        string scriptTemplate = await File.ReadAllTextAsync(scriptTemplatePath);

        string script = scriptTemplate
            .Replace("{{KUBELET_MI_CLIENT_ID}}", kubeletMiClientId)
            .Replace("{{CONFIG_JSON}}", configJson);

        // Save script locally for debugging purposes.
        var localScriptPath = Path.Combine("/tmp", $"{vmName}.install-flex-node-agent.sh");
        await File.WriteAllTextAsync(localScriptPath, script);
        this.logger.LogInformation($"Script saved locally to: {localScriptPath}");

        try
        {
            await this.ExecuteScriptViaSshAsync(
                sshSession,
                script,
                vmName,
                sshPrivateKeyPath);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                $"Flex node agent setup script failed on VM {vmName}: {ex.Message}");

            string uniqueErrors = await this.ExtractUniqueAgentErrorsAsync(
                sshSession,
                sshPrivateKeyPath);

            if (!string.IsNullOrEmpty(uniqueErrors))
            {
                throw new ApiException(new ODataError(
                    code: "FlexNodeAgentSetupFailed",
                    message: "Flex node agent setup script failed. Following errors " +
                        $"were found in the agent log file:\n{uniqueErrors}"));
            }

            throw;
        }

        this.logger.LogInformation($"Flex node agent installation completed on VM: {vmName}");
    }

    /// <summary>
    /// Reads the flex node agent log from the VM and extracts unique error lines,
    /// deduplicated by the msg= value. Mirrors the logic in install-flex-node-agent.sh.
    /// </summary>
    private async Task<string> ExtractUniqueAgentErrorsAsync(
        ISshSession sshSession,
        string sshPrivateKeyPath)
    {
        const string LogFile = "/var/log/aks-flex-node/aks-flex-node.log";

        try
        {
            // Read error lines from the agent log, extract per-line msg="..." value,
            // deduplicate by msg, and emit the first full line for each unique msg.
            string logContent = await sshSession.RunCommandWithOutputAsync(
                "azureuser",
                sshPrivateKeyPath,
                $"sudo cat {LogFile}");

            if (string.IsNullOrWhiteSpace(logContent))
            {
                return string.Empty;
            }

            var regex = new System.Text.RegularExpressions.Regex("msg=\"([^\"]*)\"");
            var seenMessages = new HashSet<string>(StringComparer.Ordinal);
            var uniqueErrorLines = new List<string>();

            foreach (string line in logContent.Split('\n'))
            {
                if (!line.Contains("level=error"))
                {
                    continue;
                }

                var match = regex.Match(line);
                string key = match.Success ? match.Groups[1].Value : line;
                if (seenMessages.Add(key))
                {
                    uniqueErrorLines.Add(line.Trim());
                }
            }

            return string.Join("\n", uniqueErrorLines);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                $"Failed to extract agent errors from VM: {ex.Message}");
            return string.Empty;
        }
    }

    private async Task<string> PullApiServerProxyPackageAsync(string vmName)
    {
        string packageUrl = ImageUtils.ApiServerProxyPackageUrl();
        this.logger.LogInformation(
            $"Pulling api-server-proxy package from {packageUrl}...");
        var oras = new OrasClient(this.logger, this.configuration);
        string packageDir = Path.Combine(
            Path.GetTempPath(), $"{vmName}-api-server-proxy-pkg");
        if (Directory.Exists(packageDir))
        {
            Directory.Delete(packageDir, recursive: true);
        }

        Directory.CreateDirectory(packageDir);
        await oras.Pull(packageUrl, packageDir);
        this.logger.LogInformation("api-server-proxy package pulled successfully.");
        return packageDir;
    }

    private async Task UninstallApiServerProxyAsync(
        ISshSession sshSession,
        string vmName,
        string sshPrivateKeyPath,
        string packageDir,
        IProgress<string> progressReporter)
    {
        const string StagingDir = "/opt/api-server-proxy-staging";

        // Check if api-server-proxy is installed. Skip uninstall on fresh VMs.
        try
        {
            await sshSession.RunCommandAsync(
                "azureuser",
                sshPrivateKeyPath,
                "systemctl is-active api-server-proxy --quiet");
        }
        catch
        {
            this.logger.LogInformation(
                $"api-server-proxy is not installed on VM: {vmName}. " +
                $"Skipping uninstall.");
            return;
        }

        this.logger.LogInformation(
            $"Uninstalling api-server-proxy on VM: {vmName}");

        string uninstallScript = await File.ReadAllTextAsync(
            Path.Combine(packageDir, "uninstall.sh"));

        this.logger.LogInformation(
            $"Creating staging directory on VM: {StagingDir}");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"sudo mkdir -p {StagingDir} && " +
            $"sudo chown azureuser:azureuser {StagingDir}");

        this.logger.LogInformation("Copying uninstall.sh to VM...");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"cat > {StagingDir}/uninstall.sh",
            uninstallScript);

        this.logger.LogInformation("Running uninstall.sh on VM...");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"sudo bash {StagingDir}/uninstall.sh");

        this.logger.LogInformation(
            $"api-server-proxy uninstalled on VM: {vmName}");
    }

    private async Task InstallApiServerProxyAsync(
        ISshSession sshSession,
        string vmName,
        string sshPrivateKeyPath,
        string signingCertPem,
        string packageDir,
        IProgress<string> progressReporter)
    {
        const string StagingDir = "/opt/api-server-proxy-staging";
        const string ProxyListenAddr = "127.0.0.1:6444";

        progressReporter.Report("Running api-server-proxy setup script on VM...");
        this.logger.LogInformation($"Installing api-server-proxy on VM: {vmName}");

        string installScript = await File.ReadAllTextAsync(
            Path.Combine(packageDir, "install.sh"));

        this.logger.LogInformation($"Creating staging directory on VM: {StagingDir}");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"sudo mkdir -p {StagingDir} && sudo chown azureuser:azureuser {StagingDir}");

        this.logger.LogInformation("Copying install.sh to VM...");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"cat > {StagingDir}/install.sh",
            installScript);

        this.logger.LogInformation("Copying api-server-proxy binary to VM...");
        byte[] binaryBytes = await File.ReadAllBytesAsync(
            Path.Combine(packageDir, "api-server-proxy"));
        string binaryBase64 = Convert.ToBase64String(binaryBytes);
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"base64 -d > {StagingDir}/api-server-proxy && " +
            $"chmod +x {StagingDir}/api-server-proxy",
            binaryBase64);

        this.logger.LogInformation("Copying signing certificate to VM...");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"cat > {StagingDir}/signing-cert.pem",
            signingCertPem);

        // Run install.sh with --local-binary, signing certificate and proxy listen address.
        this.logger.LogInformation("Running install.sh on VM...");
        await sshSession.RunCommandAsync(
            "azureuser",
            sshPrivateKeyPath,
            $"sudo bash {StagingDir}/install.sh " +
            $"--local-binary {StagingDir}/api-server-proxy " +
            $"--signing-cert-file {StagingDir}/signing-cert.pem " +
            $"--proxy-listen-addr {ProxyListenAddr}");

        this.logger.LogInformation("api-server-proxy install script completed.");

        await this.VerifyApiServerProxyDeploymentAsync(sshSession, sshPrivateKeyPath);

        this.logger.LogInformation($"api-server-proxy installed successfully on VM: {vmName}");
    }

    private async Task<ISshSession> CreateSshSessionWithRetryAsync(
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
            $"Creating SSH session to VM {vmName} " +
            $"(timeout: {maxWait.TotalMinutes} minutes)...");

        while (elapsed < maxWait)
        {
            ISshSession? session = null;
            try
            {
                session = await sshSessionFactory.CreateSessionAsync(resourceGroupResource, vm);

                this.logger.LogInformation(
                    $"SSH session established to VM {vmName}.");
                return session;
            }
            catch (Exception ex)
            {
                this.logger.LogWarning(
                    $"Failed to create SSH session: {ex.Message}");
                if (session != null)
                {
                    await session.DisposeAsync();
                }
            }

            this.logger.LogInformation(
                $"SSH not yet available for VM {vmName}, retrying in " +
                $"{retryInterval.TotalSeconds} seconds... ({elapsed.TotalSeconds}s elapsed)");
            await Task.Delay(retryInterval);
            elapsed += retryInterval;
        }

        throw new TimeoutException(
            $"SSH session to VM {vmName} was not established within " +
            $"{maxWait.TotalMinutes} minutes.");
    }

    private async Task ExecuteScriptViaSshAsync(
        ISshSession sshSession,
        string script,
        string vmName,
        string privateKeyPath)
    {
        var remoteScriptPath = $"/tmp/install-flex-node-agent-{Guid.NewGuid():N}.sh";

        try
        {
            this.logger.LogInformation(
                $"Executing script via SSH proxy on VM {vmName}...");

            // Copy the script to the remote VM.
            await sshSession.RunCommandAsync(
                "azureuser",
                privateKeyPath,
                $"cat > {remoteScriptPath}",
                script);

            this.logger.LogInformation($"Script uploaded to {remoteScriptPath} on VM {vmName}.");

            // Make the script executable and run it with sudo.
            var command = $"chmod +x {remoteScriptPath} && {remoteScriptPath}";
            this.logger.LogInformation($"Executing installation script on VM {vmName}...");

            await sshSession.RunCommandAsync("azureuser", privateKeyPath, command);

            this.logger.LogInformation(
                $"Installation script completed successfully on VM {vmName}.");

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
                this.logger.LogWarning($"Failed to clean up remote script file " +
                $"{remoteScriptPath} on VM {vmName}: {ex.Message}");
            }

            return;
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                $"Script execution via SSH failed on VM {vmName}: {ex.Message}.");
            throw;
        }
    }

    private async Task WaitForNodeToJoinClusterAsync(string vmName, KubectlClient kubectlClient)
    {
        this.logger.LogInformation($"Waiting for node '{vmName}' to join the cluster...");

        var maxWait = TimeSpan.FromMinutes(1);
        var waitInterval = TimeSpan.FromSeconds(10);
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

    private async Task VerifyApiServerProxyDeploymentAsync(
        ISshSession sshSession,
        string sshPrivateKeyPath)
    {
        this.logger.LogInformation("Verifying api-server-proxy deployment...");

        // Check api-server-proxy service status.
        try
        {
            await sshSession.RunCommandAsync(
                "azureuser",
                sshPrivateKeyPath,
                "sudo systemctl status api-server-proxy --no-pager");
            this.logger.LogInformation("api-server-proxy service is running.");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning($"api-server-proxy status check failed: {ex.Message}");
        }

        // Check recent api-server-proxy logs.
        try
        {
            await sshSession.RunCommandAsync(
                "azureuser",
                sshPrivateKeyPath,
                "sudo journalctl -u api-server-proxy --no-pager -n 20");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning($"Failed to get api-server-proxy logs: {ex.Message}");
        }

        // Check kubelet service status.
        try
        {
            await sshSession.RunCommandAsync(
                "azureuser",
                sshPrivateKeyPath,
                "sudo systemctl status kubelet --no-pager | head -15");
            this.logger.LogInformation("kubelet service is running.");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning($"kubelet status check failed: {ex.Message}");
        }
    }
}
