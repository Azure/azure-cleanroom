// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Resources;
using CcfCommon;
using CcfProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCcfProvider;

public class AciNodeProvider : ICcfNodeProvider
{
    private ILogger logger;
    private IConfiguration configuration;

    public AciNodeProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public InfraType InfraType => InfraType.virtualaci;

    public async Task<NodeEndpoint> CreateStartNode(
        string nodeName,
        string networkName,
        List<InitialMember> initialMembers,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        NodeData nodeData,
        List<string> san,
        JsonObject? providerConfig)
    {
        this.ValidateCreateInput(providerConfig);
        string containerGroupName = nodeName;

        ContainerGroupData? cgData =
            await AciUtils.TryGetContainerGroupData(containerGroupName, providerConfig!);
        if (cgData != null)
        {
            return AciUtils.ToNodeEndpoint(cgData);
        }

        // Pack the start_config, members info and constitution files in a tar gz, base64 encode it
        // and set that as an environment variable on the ACI instance. Then have a bootstrap
        // script that unpacks the tar gz file and then launches the cchost instance.
        string nodeConfigDataDir = WorkspaceDirectories.GetConfigurationDirectory(
            nodeName,
            networkName,
            this.InfraType);
        Directory.CreateDirectory(nodeConfigDataDir);

        CCHostConfig cchostConfig = await CCHostConfig.InitConfig(
            "templates/virtual/start-config.json",
            outDir: nodeConfigDataDir);

        // Set networking configuration.
        string location = providerConfig!["location"]!.ToString();
        string dnsNameLabel = this.GenerateDnsName(nodeName, networkName, providerConfig!);
        var fqdn = $"{dnsNameLabel}.{location}.azurecontainer.io";

        cchostConfig.SetPublishedAddress(fqdn);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        var altNames = fqdn.NodeSanFormat();
        altNames.AddRange(san);
        cchostConfig.SetSubjectAltNames(altNames);

        await cchostConfig.SetStartConfiguration(initialMembers, "constitution");

        // Write out the config file.
        await cchostConfig.SaveConfig();

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(nodeConfigDataDir);

        ContainerGroupData resourceData = await this.CreateContainerGroup(
            networkName,
            nodeName,
            containerGroupName,
            providerConfig,
            tgzConfigData,
            dnsNameLabel);

        return AciUtils.ToNodeEndpoint(resourceData);
    }

    public async Task<NodeEndpoint> CreateJoinNode(
        string nodeName,
        string networkName,
        string serviceCertPem,
        string targetNodeName,
        string targetRpcAddress,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        NodeData nodeData,
        List<string> san,
        JsonObject? providerConfig)
    {
        this.ValidateCreateInput(providerConfig);
        string containerGroupName = nodeName;

        ContainerGroupData? cgData =
            await AciUtils.TryGetContainerGroupData(containerGroupName, providerConfig!);
        if (cgData != null)
        {
            return AciUtils.ToNodeEndpoint(cgData);
        }

        string nodeConfigDataDir = WorkspaceDirectories.GetConfigurationDirectory(
            nodeName,
            networkName,
            this.InfraType);
        Directory.CreateDirectory(nodeConfigDataDir);

        var cchostConfig = await CCHostConfig.InitConfig(
            "templates/virtual/join-config.json",
            outDir: nodeConfigDataDir);

        string dnsNameLabel = this.GenerateDnsName(nodeName, networkName, providerConfig!);
        string location = providerConfig!["location"]!.ToString();
        var fqdn = $"{dnsNameLabel}.{location}.azurecontainer.io";

        cchostConfig.SetPublishedAddress(fqdn);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        var altNames = fqdn.NodeSanFormat();
        altNames.AddRange(san);
        cchostConfig.SetSubjectAltNames(altNames);

        await cchostConfig.SetJoinConfiguration(targetRpcAddress, serviceCertPem);
        await cchostConfig.SaveConfig();

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(nodeConfigDataDir);

        ContainerGroupData resourceData = await this.CreateContainerGroup(
            networkName,
            nodeName,
            containerGroupName,
            providerConfig,
            tgzConfigData,
            dnsNameLabel);

        return AciUtils.ToNodeEndpoint(resourceData);
    }

    public Task<NodeEndpoint> CreateRecoverNode(
        string nodeName,
        string networkName,
        string networkToRecoverName,
        string previousServiceCertPem,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        NodeData nodeData,
        List<string> san,
        JsonObject? providerConfig)
    {
        throw new NotImplementedException($"CreateRecoverNode not supported for {this.InfraType}.");
    }

    public Task<List<string>> GetCandidateRecoveryNodes(
        string networkName,
        JsonObject? providerConfig)
    {
        throw new NotImplementedException(
            $"GetCandidateRecoveryNodes not supported for {this.InfraType}.");
    }

    public async Task DeleteNodes(
        string networkName,
        DeleteOption deleteOption,
        JsonObject? providerConfig)
    {
        this.ValidateDeleteInput(providerConfig);

        List<ContainerGroupResource> nodeContainerGroups =
            await AciUtils.GetNetworkContainerGroups(networkName, "node", providerConfig);

        this.logger.LogInformation(
            $"Found {nodeContainerGroups.Count} node container groups to delete.");

        List<Task> deleteTasks = new();
        foreach (var resource in nodeContainerGroups)
        {
            deleteTasks.Add(Task.Run(async () =>
            {
                this.logger.LogInformation($"Deleting node container group {resource.Id}");
                await resource.DeleteAsync(WaitUntil.Completed);
            }));
        }

        await Task.WhenAll(deleteTasks);
    }

    public async Task DeleteNode(
        string networkName,
        string nodeName,
        NodeData nodeData,
        JsonObject? providerConfig)
    {
        this.ValidateDeleteInput(providerConfig);
        string containerGroupName = nodeName;
        var resource = await AciUtils.TryGetContainerGroup(containerGroupName, providerConfig!);
        if (resource == null)
        {
            return;
        }

        this.logger.LogInformation($"Deleting container group {resource.Id}");
        await resource.DeleteAsync(WaitUntil.Completed);
    }

    public async Task<List<NodeEndpoint>> GetNodes(string networkName, JsonObject? providerConfig)
    {
        this.ValidateGetInput(providerConfig);

        List<ContainerGroupResource> nodeContainerGroups =
            await AciUtils.GetNetworkContainerGroups(networkName, "node", providerConfig);
        return nodeContainerGroups.ConvertAll(cg => AciUtils.ToNodeEndpoint(cg.Data));
    }

    public Task<List<RecoveryAgentEndpoint>> GetRecoveryAgents(
        string networkName,
        JsonObject? providerConfig)
    {
        throw new NotImplementedException();
    }

    public async Task<List<NodeHealth>> GetNodesHealth(
        string networkName,
        JsonObject? providerConfig)
    {
        this.ValidateGetInput(providerConfig);

        List<ContainerGroupResource> nodeContainerGroups =
            await AciUtils.GetNetworkContainerGroups(networkName, "node", providerConfig);
        return nodeContainerGroups.ConvertAll(cg => AciUtils.ToNodeHealth(cg.Data));
    }

    public async Task<NodeHealth> GetNodeHealth(
    string networkName,
    string nodeName,
    JsonObject? providerConfig)
    {
        this.ValidateGetInput(providerConfig);

        string containerGroupName = nodeName;
        var cg = await AciUtils.GetContainerGroup(containerGroupName, providerConfig!);
        return AciUtils.ToNodeHealth(cg.Data);
    }

    public Task<JsonObject> GenerateSecurityPolicy(SecurityPolicyCreationOption policyOption)
    {
        return Task.FromResult(new JsonObject());
    }

    public Task<JsonObject> GenerateJoinPolicy(SecurityPolicyCreationOption policyOption)
    {
        return Task.FromResult(new JsonObject());
    }

    private void ValidateCreateInput(JsonObject? providerConfig)
    {
        if (providerConfig == null)
        {
            throw new ArgumentNullException("providerConfig must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["location"]?.ToString()))
        {
            throw new ArgumentNullException("location must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["subscriptionId"]?.ToString()))
        {
            throw new ArgumentNullException("subscriptionId must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["resourceGroupName"]?.ToString()))
        {
            throw new ArgumentNullException("resourceGroupName must be specified");
        }
    }

    private void ValidateDeleteInput(JsonObject? providerConfig)
    {
        if (providerConfig == null)
        {
            throw new ArgumentNullException("providerConfig must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["subscriptionId"]?.ToString()))
        {
            throw new ArgumentNullException("subscriptionId must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["resourceGroupName"]?.ToString()))
        {
            throw new ArgumentNullException("resourceGroupName must be specified");
        }
    }

    private void ValidateGetInput(JsonObject? providerConfig)
    {
        if (providerConfig == null)
        {
            throw new ArgumentNullException("providerConfig must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["subscriptionId"]?.ToString()))
        {
            throw new ArgumentNullException("subscriptionId must be specified");
        }

        if (string.IsNullOrEmpty(providerConfig["resourceGroupName"]?.ToString()))
        {
            throw new ArgumentNullException("resourceGroupName must be specified");
        }
    }

    private async Task<ContainerGroupData> CreateContainerGroup(
        string networkName,
        string nodeName,
        string containerGroupName,
        JsonObject providerConfig,
        string tgzConfigData,
        string dnsNameLabel)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string location = providerConfig["location"]!.ToString();
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        ContainerGroupCollection collection = resourceGroupResource.GetContainerGroups();

        ContainerGroupData data = CreateContainerGroupData(
            location,
            networkName,
            nodeName,
            dnsNameLabel,
            tgzConfigData);

        this.logger.LogInformation(
            $"Starting container group creation for node: {containerGroupName}");

        ArmOperation<ContainerGroupResource> lro = await collection.CreateOrUpdateAsync(
            WaitUntil.Completed,
            containerGroupName,
            data);
        ContainerGroupResource result = lro.Value;

        // The variable result is a resource, you could call other operations on this instance as
        // well.
        ContainerGroupData resourceData = result.Data;

        this.logger.LogInformation(
            $"container group creation succeeded. " +
            $"id: {resourceData.Id}, IP address: {resourceData.IPAddress.IP}, " +
            $"fqdn: {resourceData.IPAddress.Fqdn}");
        return resourceData;

        static ContainerGroupData CreateContainerGroupData(
            string location,
            string networkName,
            string nodeName,
            string dnsNameLabel,
            string tgzConfigData)
        {
            return new ContainerGroupData(
                new AzureLocation(location),
                new ContainerInstanceContainer[]
                {
                    new(
                        AciConstants.ContainerName.CcHost,
                        $"{ImageUtils.CcfRunJsAppVirtualImage()}:" +
                        $"{ImageUtils.CcfRunJsAppVirtualTag()}",
                        new ContainerResourceRequirements(
                            new ContainerResourceRequestsContent(1.5, 1)))
                    {
                        Ports =
                        {
                            new ContainerPort(Ports.RpcMainPort),
                            new ContainerPort(Ports.RpcDebugPort),
                            new ContainerPort(Ports.NodeToNodePort)
                        },
                        EnvironmentVariables =
                        {
                            new ContainerEnvironmentVariable("CONFIG_DATA_TGZ")
                            {
                                Value = tgzConfigData
                            }
                        }
                    }
                },
                ContainerInstanceOperatingSystemType.Linux)
            {
                RestartPolicy = ContainerGroupRestartPolicy.Never,
                Tags =
                {
                    {
                        AciConstants.CcfNetworkNameTag,
                        networkName
                    },
                    {
                        AciConstants.CcfNetworkTypeTag,
                        "node"
                    },
                    {
                        AciConstants.CcfNetworkResourceNameTag,
                        nodeName
                    }
                },
                IPAddress = new ContainerGroupIPAddress(
                    new ContainerGroupPort[]
                    {
                        new(Ports.RpcMainPort)
                        {
                            Protocol = ContainerGroupNetworkProtocol.Tcp,
                        },
                        new(Ports.NodeToNodePort)
                        {
                            Protocol = ContainerGroupNetworkProtocol.Tcp,
                        },
                        new(Ports.RpcDebugPort)
                        {
                            Protocol = ContainerGroupNetworkProtocol.Tcp
                        }
                    },
                    ContainerGroupIPAddressType.Public)
                {
                    DnsNameLabel = dnsNameLabel,
                    AutoGeneratedDomainNameLabelScope = DnsNameLabelReusePolicy.Unsecure
                },
            };
        }
    }

    private string GenerateDnsName(string nodeName, string networkName, JsonObject providerConfig)
    {
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        string uniqueString =
            Utils.GetUniqueString((subscriptionId + resourceGroupName + networkName).ToLower());
        string suffix = "-" + uniqueString;
        string dnsName = nodeName + suffix;
        if (dnsName.Length > 63)
        {
            // ACI DNS label cannot exceed 63 characters.
            dnsName = dnsName.Substring(0, 63 - suffix.Length) + suffix;
        }

        return dnsName;
    }
}
