// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

namespace CAciCcfProvider;

public class CAciNodeProvider : ICcfNodeProvider
{
    private const string ServiceFolderMountPath = "/app/service";
    private const string ServiceCertPemFilePath = $"{ServiceFolderMountPath}/service-cert.pem";

    private ILogger logger;
    private IConfiguration configuration;

    public CAciNodeProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public InfraType InfraType => InfraType.caci;

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
            this.logger.LogWarning($"Found existing {containerGroupName} instance." +
                $" Not re-creating start node.");
            return AciUtils.ToNodeEndpoint(cgData);
        }

        // Pack the start_config, members info and constitution files in a tar gz, base64 encode it
        // and set that as an environment variable on the ACI instance. Then have a bootstrap
        // script that unpacks the tar gz file and then launches the cchost instance.
        string configDataDir = WorkspaceDirectories.GetConfigurationDirectory(
            nodeName,
            networkName,
            this.InfraType);
        Directory.CreateDirectory(configDataDir);

        CCHostConfig cchostConfig = await CCHostConfig.InitConfig(
            "templates/snp/start-config.json",
            outDir: configDataDir);

        // Set networking configuration.
        string location = providerConfig!["location"]!.ToString();
        string dnsNameLabel = this.GenerateDnsName(nodeName, networkName, providerConfig);
        var fqdn = $"{dnsNameLabel}.{location}.azurecontainer.io";

        string instanceId = Guid.NewGuid().ToString();
        this.AddInfraProviderData(nodeData, instanceId);

        cchostConfig.SetPublishedAddress(fqdn);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        var altNames = fqdn.NodeSanFormat();
        altNames.AddRange(san);
        cchostConfig.SetSubjectAltNames(altNames);

        await cchostConfig.SetStartConfiguration(initialMembers, "constitution");

        // Prepare node storage.
        var nodeStorageProvider = NodeStorageProviderFactory.Create(
            networkName,
            providerConfig,
            this.InfraType,
            this.logger);
        if (await nodeStorageProvider.NodeStorageDirectoryExists(nodeName))
        {
            // If no start node container exists but the storage folder from a previous run for
            // this start node exists then delete that or else cchost startup will fail saying
            // that ledger directory already exists.
            this.logger.LogWarning($"Removing {nodeName} node storage folder from a previous " +
                $"run before creating the start node container.");
            await nodeStorageProvider.DeleteNodeStorageDirectory(nodeName);
        }

        await nodeStorageProvider.CreateNodeStorageDirectory(nodeName);
        (var rwLedgerDir, var rwSnapshotsDir, var logsDir) =
            await nodeStorageProvider.GetReadWriteLedgerSnapshotsDir(nodeName);
        cchostConfig.SetLedgerSnapshotsDirectory(rwLedgerDir.MountPath, rwSnapshotsDir.MountPath);

        // Write out the config file.
        await cchostConfig.SaveConfig();

        // Add any configuration files that the node storage provider needs during container start.
        await nodeStorageProvider.AddNodeStorageProviderConfiguration(
            configDataDir,
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs: null,
            roSnapshotsDir: null);

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(configDataDir);

        var securityPolicy =
            await this.GetContainerGroupSecurityPolicy(policyOption);

        ContainerGroupData inputData = this.CreateContainerGroupData(
            location,
            networkName,
            nodeName,
            dnsNameLabel,
            tgzConfigData,
            instanceId,
            securityPolicy);

        await nodeStorageProvider.UpdateCreateContainerGroupParams(
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs: null,
            roSnapshotsDir: null,
            inputData);

        if (providerConfig.StartNodeSleep())
        {
            inputData.Containers.Single(
                c => c.Name == AciConstants.ContainerName.CcHost).EnvironmentVariables.Add(
                new ContainerEnvironmentVariable("TAIL_DEV_NULL")
                {
                    Value = "true"
                });
        }

        if (logsDir != null)
        {
            inputData.Containers.Single(
                c => c.Name == AciConstants.ContainerName.CcHost).EnvironmentVariables.Add(
                new ContainerEnvironmentVariable("LOGS_DIR")
                {
                    Value = logsDir.MountPath
                });
        }

        ContainerGroupData resourceData = await this.CreateContainerGroup(
            containerGroupName,
            inputData,
            providerConfig);

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
            this.logger.LogWarning($"Found existing {containerGroupName} instance." +
                $" Not re-creating join node.");
            return AciUtils.ToNodeEndpoint(cgData);
        }

        var nodeStorageProvider = NodeStorageProviderFactory.Create(
            networkName,
            providerConfig,
            this.InfraType,
            this.logger);

        List<IDirectory>? roLedgerDirs = null;
        IDirectory? roSnapshotsDir = null;
        if (providerConfig.FastJoin(nodeStorageProvider.GetNodeStorageType()))
        {
            this.logger.LogInformation($"Will look for snapshots to use for joining {nodeName}.");
            (roLedgerDirs, roSnapshotsDir) =
                await nodeStorageProvider.GetReadonlyLedgerSnapshotsDir();
        }

        string nodeConfigDataDir = WorkspaceDirectories.GetConfigurationDirectory(
            nodeName,
            networkName,
            this.InfraType);
        Directory.CreateDirectory(nodeConfigDataDir);

        var cchostConfig = await CCHostConfig.InitConfig(
            "templates/snp/join-config.json",
            outDir: nodeConfigDataDir);

        string dnsNameLabel = this.GenerateDnsName(nodeName, networkName, providerConfig!);
        string location = providerConfig!["location"]!.ToString();
        var fqdn = $"{dnsNameLabel}.{location}.azurecontainer.io";

        string instanceId = Guid.NewGuid().ToString();
        this.AddInfraProviderData(nodeData, instanceId);

        cchostConfig.SetPublishedAddress(fqdn);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        var altNames = fqdn.NodeSanFormat();
        altNames.AddRange(san);
        cchostConfig.SetSubjectAltNames(altNames);

        await cchostConfig.SetJoinConfiguration(targetRpcAddress, serviceCertPem);

        // Set the ledger and snapshots directory mount paths that are mapped to the docker host.
        if (await nodeStorageProvider.NodeStorageDirectoryExists(nodeName))
        {
            this.logger.LogWarning(
                $"{nodeName} node storage folder already exists from a previous run and " +
                $"will be mounted as-is for creating the join node container.");
            await nodeStorageProvider.DeleteUncommittedLedgerFiles(nodeName);
        }

        await nodeStorageProvider.CreateNodeStorageDirectory(nodeName);

        (var rwLedgerDir, var rwSnapshotsDir, var logsDir) =
            await nodeStorageProvider.GetReadWriteLedgerSnapshotsDir(nodeName);

        // Copy the latest snapshot from RO snapshots into the joining node's storage or else
        // we may not have access to the snapshot when creating new nodes in join mode which use
        // this node as the target for joining and then  can get StartupSeqnoIsOld error if
        // new nodes start w/o a snapshot while this node acting as primary started from a
        // snapshot. We need to point the joining nodes to at least the snapshot
        // from which the recovery node started.
        await nodeStorageProvider.CopyLatestSnapshot(roSnapshotsDir, rwSnapshotsDir);

        // Not specifying roSNapshotsDirectory as roSnapshotsDir?.MountPath since we copied the
        // latest snapshot into the snapshots rw dir above.
        cchostConfig.SetLedgerSnapshotsDirectory(
            rwLedgerDir.MountPath,
            rwSnapshotsDir.MountPath,
            roLedgerDirs?.ConvertAll(d => d.MountPath),
            roSnapshotsDirectory: null);

        // Add any configuration files that the node storage provider needs during container start.
        await nodeStorageProvider.AddNodeStorageProviderConfiguration(
            nodeConfigDataDir,
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs,
            roSnapshotsDir);
        await cchostConfig.SaveConfig();

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(nodeConfigDataDir);

        var securityPolicy =
            await this.GetContainerGroupSecurityPolicy(policyOption);

        ContainerGroupData inputData = this.CreateContainerGroupData(
            location,
            networkName,
            nodeName,
            dnsNameLabel,
            tgzConfigData,
            instanceId,
            securityPolicy);

        await nodeStorageProvider.UpdateCreateContainerGroupParams(
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs,
            roSnapshotsDir,
            inputData);

        if (providerConfig.JoinNodeSleep())
        {
            inputData.Containers.Single(
                c => c.Name == AciConstants.ContainerName.CcHost).EnvironmentVariables.Add(
                new ContainerEnvironmentVariable("TAIL_DEV_NULL")
                {
                    Value = "true"
                });
        }

        if (logsDir != null)
        {
            inputData.Containers.Single(
                c => c.Name == AciConstants.ContainerName.CcHost).EnvironmentVariables.Add(
                new ContainerEnvironmentVariable("LOGS_DIR")
                {
                    Value = logsDir.MountPath
                });
        }

        ContainerGroupData resourceData = await this.CreateContainerGroup(
            containerGroupName,
            inputData,
            providerConfig);

        return AciUtils.ToNodeEndpoint(resourceData);
    }

    public async Task<NodeEndpoint> CreateRecoverNode(
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
        this.ValidateCreateInput(providerConfig);

        string containerGroupName = nodeName;
        ContainerGroupData? cgData =
            await AciUtils.TryGetContainerGroupData(containerGroupName, providerConfig!);
        if (cgData != null)
        {
            this.logger.LogWarning($"Found existing {containerGroupName} instance." +
                $" Not re-creating recover node.");
            return AciUtils.ToNodeEndpoint(cgData);
        }

        var nodeStorageProvider = NodeStorageProviderFactory.Create(
            networkName,
            providerConfig,
            this.InfraType,
            this.logger);

        List<IDirectory>? roLedgerDirs = null;
        IDirectory? roSnapshotsDir = null;
        if (providerConfig.FastJoin(nodeStorageProvider.GetNodeStorageType()))
        {
            this.logger.LogInformation($"Will look for snapshots to use for recovering " +
                $"network {networkToRecoverName} on node {nodeName} on " +
                $"target network: {networkName}.");
            (roLedgerDirs, roSnapshotsDir) =
                await nodeStorageProvider.GetReadonlyLedgerSnapshotsDirForNetwork(
                    networkToRecoverName);
        }

        string nodeConfigDataDir = WorkspaceDirectories.GetConfigurationDirectory(
            nodeName,
            networkName,
            this.InfraType);
        Directory.CreateDirectory(nodeConfigDataDir);

        var cchostConfig = await CCHostConfig.InitConfig(
            "templates/snp/recover-config.json",
            outDir: nodeConfigDataDir);

        string dnsNameLabel = this.GenerateDnsName(nodeName, networkName, providerConfig!);
        string location = providerConfig!["location"]!.ToString();
        var fqdn = $"{dnsNameLabel}.{location}.azurecontainer.io";

        string instanceId = Guid.NewGuid().ToString();
        this.AddInfraProviderData(nodeData, instanceId);

        cchostConfig.SetPublishedAddress(fqdn);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        var altNames = fqdn.NodeSanFormat();
        altNames.AddRange(san);
        cchostConfig.SetSubjectAltNames(altNames);

        await cchostConfig.SetRecoverConfiguration(previousServiceCertPem);

        // Set the ledger and snapshots directory mount paths that are mapped to the docker host.
        if (await nodeStorageProvider.NodeStorageDirectoryExists(nodeName))
        {
            this.logger.LogWarning(
                $"{nodeName} node storage folder already exists from a previous run and " +
                $"will be mounted as-is for creating the recover node container.");
        }

        await nodeStorageProvider.CreateNodeStorageDirectory(nodeName);

        (var rwLedgerDir, var rwSnapshotsDir, var logsDir) =
            await nodeStorageProvider.GetReadWriteLedgerSnapshotsDir(nodeName);

        // Copy the latest snapshot from RO snapshots into the recovery node's storage or else
        // we won't have access to the snapshots when creating new nodes in join mode and
        // can get StartupSeqnoIsOld error if new nodes start w/o a snapshot while the primary
        // started from a snapshot. We need to point the joining nodes to at least the snapshot
        // from which the recovery node started.
        await nodeStorageProvider.CopyLatestSnapshot(roSnapshotsDir, rwSnapshotsDir);

        cchostConfig.SetLedgerSnapshotsDirectory(
            rwLedgerDir.MountPath,
            rwSnapshotsDir.MountPath,
            roLedgerDirs?.ConvertAll(d => d.MountPath),
            roSnapshotsDir?.MountPath);

        // Add any configuration files that the node storage provider needs during container start.
        await nodeStorageProvider.AddNodeStorageProviderConfiguration(
            nodeConfigDataDir,
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs,
            roSnapshotsDir);
        await cchostConfig.SaveConfig();

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(nodeConfigDataDir);

        var securityPolicy =
            await this.GetContainerGroupSecurityPolicy(policyOption);

        ContainerGroupData inputData = this.CreateContainerGroupData(
            location,
            networkName,
            nodeName,
            dnsNameLabel,
            tgzConfigData,
            instanceId,
            securityPolicy);

        await nodeStorageProvider.UpdateCreateContainerGroupParams(
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs,
            roSnapshotsDir,
            inputData);

        if (providerConfig.JoinNodeSleep())
        {
            inputData.Containers.Single(
                c => c.Name == AciConstants.ContainerName.CcHost).EnvironmentVariables.Add(
                new ContainerEnvironmentVariable("TAIL_DEV_NULL")
                {
                    Value = "true"
                });
        }

        if (logsDir != null)
        {
            inputData.Containers.Single(
                c => c.Name == AciConstants.ContainerName.CcHost).EnvironmentVariables.Add(
                new ContainerEnvironmentVariable("LOGS_DIR")
                {
                    Value = logsDir.MountPath
                });
        }

        ContainerGroupData resourceData = await this.CreateContainerGroup(
            containerGroupName,
            inputData,
            providerConfig);

        return AciUtils.ToNodeEndpoint(resourceData);
    }

    public Task<List<string>> GetCandidateRecoveryNodes(
        string networkName,
        JsonObject? providerConfig)
    {
        var nodeStorageProvider = NodeStorageProviderFactory.Create(
            networkName,
            providerConfig,
            this.InfraType,
            this.logger);
        return nodeStorageProvider.GetNodesWithLedgers();
    }

    public async Task DeleteNodes(
        string networkName,
        DeleteOption deleteOption,
        JsonObject? providerConfig)
    {
        this.ValidateDeleteInput(providerConfig);

        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        ContainerGroupCollection collection = resourceGroupResource.GetContainerGroups();
        List<ContainerGroupResource> containerGroupsToDelete = new();
        await foreach (var item in collection.GetAllAsync())
        {
            if (item.Data.Tags.TryGetValue(AciConstants.CcfNetworkNameTag, out var nwTagValue) &&
                nwTagValue == networkName &&
                item.Data.Tags.TryGetValue(AciConstants.CcfNetworkTypeTag, out var nwTypeValue) &&
                nwTypeValue == "node")
            {
                containerGroupsToDelete.Add(item);
            }
        }

        this.logger.LogInformation(
            $"Found {containerGroupsToDelete.Count} node container groups to delete.");

        List<Task> deleteTasks = new();
        foreach (var resource in containerGroupsToDelete)
        {
            deleteTasks.Add(Task.Run(async () =>
            {
                this.logger.LogInformation($"Deleting node container group {resource.Id}");
                await resource.DeleteAsync(WaitUntil.Completed);
            }));
        }

        await Task.WhenAll(deleteTasks);

        var networkDir = WorkspaceDirectories.GetNetworkDirectory(networkName, this.InfraType);
        if (deleteOption == DeleteOption.DeleteStorage)
        {
            if (Directory.Exists(networkDir))
            {
                this.logger.LogWarning($"Removing {networkDir} folder for this network.");
                Directory.Delete(networkDir, recursive: true);
            }
        }
        else
        {
            this.logger.LogWarning($"Not removing {networkDir} folder as deleteOption " +
                $"is {deleteOption}.");
        }

        if (deleteOption == DeleteOption.DeleteStorage)
        {
            if (providerConfig.GetNodeStorageType(this.InfraType) == NodeStorageType.AzureFiles)
            {
                await AzFileShare.DeleteFileShares(
                    networkName,
                    providerConfig!,
                    this.logger.ProgressReporter());
            }
        }
        else
        {
            this.logger.LogWarning($"Not removing ledger/snapshots directory from storage " +
                $"as deleteOption is {deleteOption}.");
        }
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

        var instanceId = InfraProviderNodeData.FromObject(nodeData.InfraProviderData)?.InstanceId;
        if (!string.IsNullOrEmpty(instanceId))
        {
            string? value = null;
            resource.Data.Tags?.TryGetValue(AciConstants.CcfNetworkInstanceIdTag, out value);
            if (value == instanceId)
            {
                this.logger.LogInformation($"Deleting container group {resource.Id}.");
                await resource.DeleteAsync(WaitUntil.Completed);
            }
            else
            {
                this.logger.LogWarning($"Not deleting container group {resource.Id} as " +
                    $"nodeData's instanceId value '{instanceId}' does not match tag value " +
                    $"'{value}'");
            }
        }
        else
        {
            this.logger.LogWarning(
                $"Not deleting container group {resource.Id} as instanceId " +
                $"value in nodeData is not available {JsonSerializer.Serialize(nodeData)}.");
            await resource.DeleteAsync(WaitUntil.Completed);
        }
    }

    public async Task<List<NodeEndpoint>> GetNodes(string networkName, JsonObject? providerConfig)
    {
        this.ValidateGetInput(providerConfig);

        List<ContainerGroupResource> nodeContainerGroups =
            await this.GetContainerGroups(networkName, "node", providerConfig);
        return nodeContainerGroups.ConvertAll(cg => AciUtils.ToNodeEndpoint(cg.Data));
    }

    public async Task<List<RecoveryAgentEndpoint>> GetRecoveryAgents(
        string networkName,
        JsonObject? providerConfig)
    {
        this.ValidateGetInput(providerConfig);

        List<ContainerGroupResource> nodeContainerGroups =
            await this.GetContainerGroups(networkName, "node", providerConfig);
        return nodeContainerGroups.ConvertAll(cg => AciUtils.ToRecoveryAgentEndpoint(cg.Data));
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

    public async Task<JsonObject> GenerateSecurityPolicy(SecurityPolicyCreationOption policyOption)
    {
        string policyRego;
        if (policyOption == SecurityPolicyCreationOption.allowAll)
        {
            policyRego =
                Encoding.UTF8.GetString(Convert.FromBase64String(AciConstants.AllowAllRegoBase64));
        }
        else
        {
            SecurityPolicyDocument policyDocument =
                await ImageUtils.GetNetworkSecurityPolicyDocument(this.logger);
            policyRego = policyOption == SecurityPolicyCreationOption.cached ? policyDocument.Rego :
                policyOption == SecurityPolicyCreationOption.cachedDebug ? policyDocument.RegoDebug :
                    throw new ArgumentException($"Unhandled policyOption: {policyOption}");
        }

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(policyRego));
        var securityPolicyDigest = BitConverter.ToString(hashBytes)
            .Replace("-", string.Empty).ToLower();

        var policy = new JsonObject
        {
            ["snp"] = new JsonObject
            {
                ["securityPolicyCreationOption"] = policyOption.ToString(),
                ["hostData"] = new JsonObject
                {
                    [securityPolicyDigest] = policyRego
                }
            }
        };

        return policy;
    }

    public async Task<JsonObject> GenerateJoinPolicy(SecurityPolicyCreationOption policyOption)
    {
        string policyRego;
        if (policyOption == SecurityPolicyCreationOption.allowAll)
        {
            policyRego =
                Encoding.UTF8.GetString(Convert.FromBase64String(AciConstants.AllowAllRegoBase64));
        }
        else
        {
            SecurityPolicyDocument policyDocument =
            await ImageUtils.GetNetworkSecurityPolicyDocument(this.logger);
            policyRego = policyOption == SecurityPolicyCreationOption.cached ? policyDocument.Rego :
                policyOption == SecurityPolicyCreationOption.cachedDebug ? policyDocument.RegoDebug :
                    throw new ArgumentException($"Unhandled policyOption: {policyOption}");
        }

        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(policyRego));
        var securityPolicyDigest = BitConverter.ToString(hashBytes)
            .Replace("-", string.Empty).ToLower();

        var policy = new JsonObject
        {
            ["snp"] = new JsonObject
            {
                ["hostData"] = new JsonArray(securityPolicyDigest)
            }
        };

        return policy;
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

    private async Task<List<ContainerGroupResource>> GetContainerGroups(
        string networkName,
        string type,
        JsonObject? providerConfig)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        ContainerGroupCollection collection = resourceGroupResource.GetContainerGroups();
        List<ContainerGroupResource> containerGroups = new();
        await foreach (var item in collection.GetAllAsync())
        {
            if (item.Data.Tags.TryGetValue(AciConstants.CcfNetworkNameTag, out var nwTagValue) &&
                nwTagValue == networkName &&
                item.Data.Tags.TryGetValue(AciConstants.CcfNetworkTypeTag, out var nwTypeValue) &&
                nwTypeValue == type)
            {
                containerGroups.Add(item);
            }
        }

        return containerGroups;
    }

    private async Task<ContainerGroupData> CreateContainerGroup(
        string containerGroupName,
        ContainerGroupData data,
        JsonObject? providerConfig)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        ContainerGroupCollection collection = resourceGroupResource.GetContainerGroups();

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
    }

    private async Task<ContainerGroupSecurityPolicy> GetContainerGroupSecurityPolicy(
        SecurityPolicyConfiguration policyOption)
    {
        this.logger.LogInformation($"policyCreationOption: {policyOption.PolicyCreationOption}");
        if (policyOption.PolicyCreationOption == SecurityPolicyCreationOption.allowAll ||
            policyOption.PolicyCreationOption == SecurityPolicyCreationOption.userSupplied)
        {
            var ccePolicyInput = policyOption.PolicyCreationOption ==
                SecurityPolicyCreationOption.allowAll ?
                AciConstants.AllowAllRegoBase64 : policyOption.Policy!;
            return new ContainerGroupSecurityPolicy
            {
                ConfidentialComputeCcePolicy = ccePolicyInput,
                Images = new()
                {
                    {
                        AciConstants.ContainerName.CcHost,
                        $"{ImageUtils.CcfRunJsAppSnpImage()}:{ImageUtils.CcfRunJsAppSnpTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrAttestation,
                        $"{ImageUtils.CcrAttestationImage()}:{ImageUtils.CcrAttestationTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcfRecoveryAgent,
                        $"{ImageUtils.CcfRecoveryAgentImage()}:{ImageUtils.CcfRecoveryAgentTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrProxy,
                        $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}"
                    }
                }
            };
        }

        (var policyRego, var policyDocument) =
            await this.DownloadAndExpandPolicy(policyOption.PolicyCreationOption);
        var ccePolicy = Convert.ToBase64String(Encoding.UTF8.GetBytes(policyRego));

        var policyContainers = policyDocument.Containers.ToDictionary(x => x.Name, x => x);
        List<string> requiredContainers =
            [
                AciConstants.ContainerName.CcHost,
                AciConstants.ContainerName.CcrAttestation,
                AciConstants.ContainerName.CcfRecoveryAgent,
                AciConstants.ContainerName.CcrProxy
            ];
        var missingContainers = requiredContainers.Where(r => !policyContainers.ContainsKey(r));
        if (missingContainers.Any())
        {
            throw new Exception(
                $"Policy document is missing the following required containers: " +
                $"{JsonSerializer.Serialize(missingContainers)}");
        }

        var securityPolicy = new ContainerGroupSecurityPolicy
        {
            ConfidentialComputeCcePolicy = ccePolicy,
            Images = []
        };

        foreach (var containerName in requiredContainers)
        {
            var pc = policyContainers[containerName];
            securityPolicy.Images.Add(containerName, $"{pc.Image}@{pc.Digest}");
        }

        return securityPolicy;
    }

    private ContainerGroupData CreateContainerGroupData(
        string location,
        string networkName,
        string nodeName,
        string dnsNameLabel,
        string tgzConfigData,
        string instanceId,
        ContainerGroupSecurityPolicy securityPolicy)
    {
        return new ContainerGroupData(
            new AzureLocation(location),
            new ContainerInstanceContainer[]
            {
                new(
                    AciConstants.ContainerName.CcHost,
                    securityPolicy.Images[AciConstants.ContainerName.CcHost],
                    new ContainerResourceRequirements(
                        new ContainerResourceRequestsContent(1.5, 1)))
                {
                    Ports =
                    {
                        new ContainerPort(Ports.RpcMainPort),
                        new ContainerPort(Ports.NodeToNodePort),
                        new ContainerPort(Ports.RpcDebugPort)
                    },
                    EnvironmentVariables =
                    {
                        new ContainerEnvironmentVariable("CONFIG_DATA_TGZ")
                        {
                            Value = tgzConfigData
                        }
                    }
                },
                new(
                    AciConstants.ContainerName.CcrAttestation,
                    securityPolicy.Images[AciConstants.ContainerName.CcrAttestation],
                    new ContainerResourceRequirements(
                        new ContainerResourceRequestsContent(0.5, 0.2)))
                {
                    Command =
                    {
                        "app",
                        "-socket-address",
                        "/mnt/uds/sock"
                    },
                    VolumeMounts =
                    {
                        new ContainerVolumeMount("uds", "/mnt/uds")
                    }
                },
                new(
                    AciConstants.ContainerName.CcfRecoveryAgent,
                    securityPolicy.Images[AciConstants.ContainerName.CcfRecoveryAgent],
                    new ContainerResourceRequirements(
                        new ContainerResourceRequestsContent(0.5, 0.2)))
                {
                    EnvironmentVariables =
                    {
                        new ContainerEnvironmentVariable("CCF_ENDPOINT")
                        {
                            Value = $"localhost:{Ports.RpcMainPort}"
                        },
                        new ContainerEnvironmentVariable("CCF_ENDPOINT_SKIP_TLS_VERIFY")
                        {
                            Value = "true"
                        },
                        new ContainerEnvironmentVariable("ASPNETCORE_URLS")
                        {
                            Value = $"http://+:{Ports.RecoveryAgentPort}"
                        }
                    },
                    VolumeMounts =
                    {
                        new ContainerVolumeMount("uds", "/mnt/uds"),
                        new ContainerVolumeMount("shared", "/app/service")
                    }
                },
                new(
                    AciConstants.ContainerName.CcrProxy,
                    securityPolicy.Images[AciConstants.ContainerName.CcrProxy],
                    new ContainerResourceRequirements(
                        new ContainerResourceRequestsContent(0.5, 0.2)))
                {
                    Ports =
                    {
                        new ContainerPort(Ports.EnvoyPort)
                    },
                    Command =
                    {
                        "/bin/sh",
                        "https-http/bootstrap.sh"
                    },
                    EnvironmentVariables =
                    {
                        new ContainerEnvironmentVariable("CCR_ENVOY_DESTINATION_PORT")
                        {
                            Value = Ports.RecoveryAgentPort.ToString()
                        },
                        new ContainerEnvironmentVariable("CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE")
                        {
                            Value = ServiceCertPemFilePath
                        }
                    },
                    VolumeMounts =
                    {
                        new ContainerVolumeMount("shared", ServiceFolderMountPath)
                    }
                },
            },
            ContainerInstanceOperatingSystemType.Linux)
        {
            RestartPolicy = ContainerGroupRestartPolicy.Never,
            Sku = ContainerGroupSku.Confidential,
            ConfidentialComputeCcePolicy = securityPolicy.ConfidentialComputeCcePolicy,
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
                },
                {
                    AciConstants.CcfNetworkInstanceIdTag,
                    instanceId
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
                        Protocol = ContainerGroupNetworkProtocol.Tcp,
                    },
                    new(Ports.EnvoyPort)
                    {
                        Protocol = ContainerGroupNetworkProtocol.Tcp,
                    }
                },
                ContainerGroupIPAddressType.Public)
            {
                DnsNameLabel = dnsNameLabel,
                AutoGeneratedDomainNameLabelScope = DnsNameLabelReusePolicy.Unsecure
            },
            Volumes =
            {
                new ContainerVolume("uds")
                {
                    EmptyDir = BinaryData.FromObjectAsJson(new Dictionary<string, object>())
                },
                new ContainerVolume("shared")
                {
                    EmptyDir = BinaryData.FromObjectAsJson(new Dictionary<string, object>())
                }
            }
        };
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

    private void AddInfraProviderData(NodeData nodeData, string instanceId)
    {
        nodeData.InfraProviderData = new InfraProviderNodeData
        {
            SecurityPolicyUrl = ImageUtils.CcfNetworkSecurityPolicyDocumentUrl(),
            InstanceId = instanceId
        }.AsObject();
    }

    private async Task<(string, SecurityPolicyDocument)> DownloadAndExpandPolicy(
        SecurityPolicyCreationOption policyCreationOption)
    {
        var policyDocument = await ImageUtils.GetNetworkSecurityPolicyDocument(this.logger);

        foreach (var container in policyDocument.Containers)
        {
            container.Image = container.Image.Replace("@@RegistryUrl@@", ImageUtils.RegistryUrl());
        }

        var policyRego =
            policyCreationOption == SecurityPolicyCreationOption.cachedDebug ?
            policyDocument.RegoDebug :
            policyCreationOption == SecurityPolicyCreationOption.cached ? policyDocument.Rego :
                throw new ArgumentException($"Unexpected option: {policyCreationOption}");
        return (policyRego, policyDocument);
    }

    public class InfraProviderNodeData
    {
        [JsonPropertyName("securityPolicyUrl")]
        public string SecurityPolicyUrl { get; set; } = default!;

        [JsonPropertyName("instanceId")]
        public string InstanceId { get; set; } = default!;

        public static InfraProviderNodeData? FromObject(JsonObject? obj)
        {
            if (obj != null)
            {
                return JsonSerializer.Deserialize<InfraProviderNodeData>(
                    JsonSerializer.Serialize(obj));
            }

            return null;
        }

        public JsonObject AsObject()
        {
            return JsonSerializer.Deserialize<JsonObject>(JsonSerializer.Serialize(this))!;
        }
    }
}
