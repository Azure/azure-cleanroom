// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using CcfCommon;
using CcfProvider;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static VirtualCcfProvider.DockerRecoveryServiceInstanceProvider;
using NodeStatus = CcfProvider.NodeStatus;

namespace VirtualCcfProvider;

public class DockerNodeProvider : ICcfNodeProvider
{
    private ILogger logger;
    private IConfiguration configuration;
    private DockerClient client;

    public DockerNodeProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
    }

    public InfraType InfraType => InfraType.@virtual;

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
        string containerName = "cchost-nw-" + nodeName;

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

        cchostConfig.SetPublishedAddress(fqdn: containerName);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        cchostConfig.SetSubjectAltNames(san);

        await cchostConfig.SetStartConfiguration(initialMembers, "constitution");

        // Prepare node storage.
        var nodeStorageProvider = NodeStorageProviderFactory.Create(
            networkName,
            providerConfig,
            this.InfraType,
            this.logger);
        if (!await this.client.ContainerExists(containerName) &&
            await nodeStorageProvider.NodeStorageDirectoryExists(nodeName))
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
            nodeConfigDataDir,
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs: null,
            roSnapshotsDir: null);

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(nodeConfigDataDir);

        try
        {
            await this.client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = networkName
            });
        }
        catch (DockerApiException de) when
        (de.ResponseBody.Contains($"network with name {networkName} already exists"))
        {
            // Ignore already exists.
        }

        var createContainerParams = new CreateContainerParameters
        {
            Labels = new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfNetworkNameTag,
                    networkName
                },
                {
                    DockerConstants.CcfNetworkTypeTag,
                    "node"
                },
                {
                    DockerConstants.CcfNetworkResourceNameTag,
                    nodeName
                }
            },
            Name = containerName,
            Image = $"{ImageUtils.CcfRunJsAppVirtualImage()}:{ImageUtils.CcfRunJsAppVirtualTag()}",
            Env =
            [
                $"CONFIG_DATA_TGZ={tgzConfigData}"
            ],
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.RpcMainPort}/tcp", new EmptyStruct()
                },
                {
                    $"{Ports.NodeToNodePort}/tcp", new EmptyStruct()
                },
                {
                    $"{Ports.RpcDebugPort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = networkName,
                Devices = new List<DeviceMapping>(), // Allocate these as might be modified later.
                CapAdd = new List<string>(),
                Binds = new List<string>(),
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{Ports.RpcMainPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                    // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    },
                    {
                        $"{Ports.RpcDebugPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                    // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    }
                }
            }
        };

        if (providerConfig.StartNodeSleep())
        {
            createContainerParams.Env.Add("TAIL_DEV_NULL=true");
        }

        if (logsDir != null)
        {
            createContainerParams.Env.Add($"LOGS_DIR={logsDir.MountPath}");
        }

        await nodeStorageProvider.UpdateCreateContainerParams(
            nodeName,
            roLedgerDirs: null,
            roSnapshotsDir: null,
            createContainerParams);

        NodeEndpoint nodeEndpoint = await this.CreateAndStartNodeContainer(createContainerParams);
        await this.CreateAndStartRecoveryAgentContainer(networkName, nodeEndpoint);
        return nodeEndpoint;
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
            "templates/virtual/join-config.json",
            outDir: nodeConfigDataDir);

        string containerName = "cchost-nw-" + nodeName;

        cchostConfig.SetPublishedAddress(fqdn: containerName);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        cchostConfig.SetSubjectAltNames(san);

        await cchostConfig.SetJoinConfiguration(targetRpcAddress, serviceCertPem);

        // Set the ledger and snapshots directory mount paths that are mapped to the docker host.
        if (!await this.client.ContainerExists(containerName) &&
            await nodeStorageProvider.NodeStorageDirectoryExists(nodeName))
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

        await cchostConfig.SaveConfig();

        // Add any configuration files that the node storage provider needs during container start.
        await nodeStorageProvider.AddNodeStorageProviderConfiguration(
            nodeConfigDataDir,
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs,
            roSnapshotsDir);

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(nodeConfigDataDir);

        var createContainerParams = new CreateContainerParameters
        {
            Labels = new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfNetworkNameTag,
                    networkName
                },
                {
                    DockerConstants.CcfNetworkTypeTag,
                    "node"
                },
                {
                    DockerConstants.CcfNetworkResourceNameTag,
                    nodeName
                }
            },
            Name = containerName,
            Image = $"{ImageUtils.CcfRunJsAppVirtualImage()}:{ImageUtils.CcfRunJsAppVirtualTag()}",
            Env =
            [
                $"CONFIG_DATA_TGZ={tgzConfigData}"
            ],
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.RpcMainPort}/tcp", new EmptyStruct()
                },
                {
                    $"{Ports.NodeToNodePort}/tcp", new EmptyStruct()
                },
                {
                    $"{Ports.RpcDebugPort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                Devices = new List<DeviceMapping>(),
                CapAdd = new List<string>(),
                NetworkMode = networkName,
                Binds = new List<string>(),
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{Ports.RpcMainPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                    // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    },
                    {
                        $"{Ports.RpcDebugPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                    // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    }
                }
            }
        };

        if (providerConfig.JoinNodeSleep())
        {
            createContainerParams.Env.Add("TAIL_DEV_NULL=true");
        }

        if (logsDir != null)
        {
            createContainerParams.Env.Add($"LOGS_DIR={logsDir.MountPath}");
        }

        await nodeStorageProvider.UpdateCreateContainerParams(
            nodeName,
            roLedgerDirs,
            roSnapshotsDir,
            createContainerParams);

        NodeEndpoint nodeEndpoint = await this.CreateAndStartNodeContainer(createContainerParams);
        await this.CreateAndStartRecoveryAgentContainer(networkName, nodeEndpoint);
        return nodeEndpoint;
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
            "templates/virtual/recover-config.json",
            outDir: nodeConfigDataDir);

        string containerName = "cchost-nw-" + nodeName;

        cchostConfig.SetPublishedAddress(fqdn: containerName);
        cchostConfig.SetNodeLogLevel(nodeLogLevel);
        await cchostConfig.SetNodeData(nodeData);
        cchostConfig.SetSubjectAltNames(san);

        await cchostConfig.SetRecoverConfiguration(previousServiceCertPem);

        // Set the ledger and snapshots directory mount paths that are mapped to the docker host.
        if (!await this.client.ContainerExists(containerName) &&
            await nodeStorageProvider.NodeStorageDirectoryExists(nodeName))
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

        // Not specifying roSNapshotsDirectory as roSnapshotsDir?.MountPath since we copied the
        // latest snapshot into the snapshots rw dir above.
        cchostConfig.SetLedgerSnapshotsDirectory(
            rwLedgerDir.MountPath,
            rwSnapshotsDir.MountPath,
            roLedgerDirs?.ConvertAll(d => d.MountPath),
            roSnapshotsDirectory: null);

        await cchostConfig.SaveConfig();

        // Add any configuration files that the node storage provider needs during container start.
        await nodeStorageProvider.AddNodeStorageProviderConfiguration(
            nodeConfigDataDir,
            rwLedgerDir,
            rwSnapshotsDir,
            roLedgerDirs,
            roSnapshotsDir);

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(nodeConfigDataDir);

        try
        {
            await this.client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = networkName
            });
        }
        catch (DockerApiException de) when
        (de.ResponseBody.Contains($"network with name {networkName} already exists"))
        {
            // Ignore already exists.
        }

        var createContainerParams = new CreateContainerParameters
        {
            Labels = new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfNetworkNameTag,
                    networkName
                },
                {
                    DockerConstants.CcfNetworkTypeTag,
                    "node"
                },
                {
                    DockerConstants.CcfNetworkResourceNameTag,
                    nodeName
                }
            },
            Name = containerName,
            Image = $"{ImageUtils.CcfRunJsAppVirtualImage()}:{ImageUtils.CcfRunJsAppVirtualTag()}",
            Env =
            [
                $"CONFIG_DATA_TGZ={tgzConfigData}"
            ],
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.RpcMainPort}/tcp", new EmptyStruct()
                },
                {
                    $"{Ports.NodeToNodePort}/tcp", new EmptyStruct()
                },
                {
                    $"{Ports.RpcDebugPort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                Devices = new List<DeviceMapping>(),
                CapAdd = new List<string>(),
                NetworkMode = networkName,
                Binds = new List<string>(),
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{Ports.RpcMainPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                    // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    },
                    {
                        $"{Ports.RpcDebugPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                    // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    }
                }
            }
        };

        if (providerConfig.JoinNodeSleep())
        {
            createContainerParams.Env.Add("TAIL_DEV_NULL=true");
        }

        if (logsDir != null)
        {
            createContainerParams.Env.Add($"LOGS_DIR={logsDir.MountPath}");
        }

        await nodeStorageProvider.UpdateCreateContainerParams(
            nodeName,
            roLedgerDirs,
            roSnapshotsDir,
            createContainerParams);

        NodeEndpoint nodeEndpoint = await this.CreateAndStartNodeContainer(createContainerParams);
        await this.CreateAndStartRecoveryAgentContainer(networkName, nodeEndpoint);
        return nodeEndpoint;
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
        List<Task> tasks =
        [
            this.client.DeleteContainers(
                this.logger,
                filters: new Dictionary<string, IDictionary<string, bool>>
            {
                {
                    "label", new Dictionary<string, bool>
                    {
                        { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                        { $"{DockerConstants.CcfNetworkTypeTag}=node", true }
                    }
                }
            }),
            this.client.DeleteContainers(
                this.logger,
                filters: new Dictionary<string, IDictionary<string, bool>>
            {
                {
                    "label", new Dictionary<string, bool>
                    {
                        { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                        { $"{DockerConstants.CcfNetworkTypeTag}=recovery-agent", true }
                    }
                }
            }),
            this.client.DeleteContainers(
                this.logger,
                filters: new Dictionary<string, IDictionary<string, bool>>
            {
                {
                    "label", new Dictionary<string, bool>
                    {
                        { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                        { $"{DockerConstants.CcfNetworkTypeTag}=ccr-proxy", true }
                    }
                }
            }),
        ];

        await Task.WhenAll(tasks);
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
    }

    public async Task DeleteNode(
        string networkName,
        string nodeName,
        NodeData nodeData,
        JsonObject? providerConfig)
    {
        List<Task> tasks =
        [
            this.client.DeleteContainers(
                this.logger,
                filters: new Dictionary<string, IDictionary<string, bool>>
            {
                {
                    "label", new Dictionary<string, bool>
                    {
                        { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                        { $"{DockerConstants.CcfNetworkTypeTag}=node", true },
                        { $"{DockerConstants.CcfNetworkResourceNameTag}={nodeName}", true }
                    }
                }
            }),
            this.client.DeleteContainers(
                this.logger,
                filters: new Dictionary<string, IDictionary<string, bool>>
            {
                {
                    "label", new Dictionary<string, bool>
                    {
                        { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                        { $"{DockerConstants.CcfNetworkTypeTag}=recovery-agent", true },
                        { $"{DockerConstants.CcfNetworkResourceNameTag}={nodeName}", true }
                    }
                }
            }),
            this.client.DeleteContainers(
                this.logger,
                filters: new Dictionary<string, IDictionary<string, bool>>
            {
                {
                    "label", new Dictionary<string, bool>
                    {
                        { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                        { $"{DockerConstants.CcfNetworkTypeTag}=ccr-proxy", true },
                        { $"{DockerConstants.CcfNetworkResourceNameTag}={nodeName}", true }
                    }
                }
            }),
        ];

        await Task.WhenAll(tasks);
    }

    public async Task<List<NodeEndpoint>> GetNodes(string networkName, JsonObject? providerConfig)
    {
        var containers = await this.client.GetContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                    { $"{DockerConstants.CcfNetworkTypeTag}=node", true }
                }
            }
        });

        List<NodeEndpoint> nodeEndpoints = new();
        foreach (var container in containers)
        {
            nodeEndpoints.Add(container.ToNodeEndpoint());
        }

        return nodeEndpoints;
    }

    public async Task<List<RecoveryAgentEndpoint>> GetRecoveryAgents(
        string networkName,
        JsonObject? providerConfig)
    {
        // As envoy fronts the calls return its endpoint details.
        var containers = await this.client.GetContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                    { $"{DockerConstants.CcfNetworkTypeTag}=ccr-proxy", true }
                }
            }
        });

        List<RecoveryAgentEndpoint> agentEndpoints = new();
        foreach (var container in containers)
        {
            var ep = container.ToEnvoyEndpoint(DockerConstants.CcfNetworkResourceNameTag);
            agentEndpoints.Add(ep.ToRecoveryAgentEndpoint());
        }

        return agentEndpoints;
    }

    public async Task<List<NodeHealth>> GetNodesHealth(
        string networkName,
        JsonObject? providerConfig)
    {
        var containers = await this.client.GetContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                    { $"{DockerConstants.CcfNetworkTypeTag}=node", true },
                }
            }
        });

        return containers.ConvertAll(this.ToNodeHealth);
    }

    public async Task<NodeHealth> GetNodeHealth(
        string networkName,
        string nodeName,
        JsonObject? providerConfig)
    {
        var containers = await this.client.GetContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                    { $"{DockerConstants.CcfNetworkTypeTag}=node", true },
                    { $"{DockerConstants.CcfNetworkResourceNameTag}={nodeName}", true }
                }
            }
        });

        if (containers.Count != 1)
        {
            throw new Exception($"Container {nodeName} not found.");
        }

        return this.ToNodeHealth(containers[0]);
    }

    public Task<JsonObject> GenerateSecurityPolicy(
        SecurityPolicyCreationOption policyOption)
    {
        var policyRego = Encoding.UTF8.GetString(Convert.FromBase64String(
            AciConstants.AllowAllRegoBase64));
        var policy = new JsonObject
        {
            ["snp"] = new JsonObject
            {
                ["securityPolicyCreationOption"] = policyOption.ToString(),
                ["hostData"] = new JsonObject
                {
                    ["73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20"] =
                        policyRego
                }
            }
        };

        return Task.FromResult(policy);
    }

    public Task<JsonObject> GenerateJoinPolicy(
        SecurityPolicyCreationOption policyOption)
    {
        var policy = new JsonObject
        {
            ["snp"] = new JsonObject
            {
                ["hostData"] =
                new JsonArray("73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20")
            }
        };

        return Task.FromResult(policy);
    }

    private NodeHealth ToNodeHealth(ContainerListResponse container)
    {
        var status = NodeStatus.Ok;
        var reasons = new List<Reason>();
        if (container.State == "exited")
        {
            status = NodeStatus.NeedsReplacement;
            var code = "ContainerExited";
            var message = $"Container {container.ID} has exited: {container.Status}.";
            reasons.Add(new() { Code = code, Message = message });
        }

        var ep = container.ToNodeEndpoint();
        return new NodeHealth
        {
            Name = ep.NodeName,
            Endpoint = ep.ClientRpcAddress,
            Status = status.ToString(),
            Reasons = reasons
        };
    }

    private async Task<NodeEndpoint> CreateAndStartNodeContainer(
        CreateContainerParameters createParams)
    {
        await this.client.Images.CreateImageAsync(
            new ImagesCreateParameters
            {
                FromImage = ImageUtils.CcfRunJsAppVirtualImage(),
                Tag = ImageUtils.CcfRunJsAppVirtualTag(),
            },
            authConfig: null,
            new Progress<JSONMessage>(m => this.logger.LogInformation(m.ToProgressMessage())));

        this.logger.LogInformation($"Creating container: {createParams.Name}");
        var container = await this.client.CreateOrGetContainer(createParams);
        await this.client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        // Fetch again after starting to get the port mapping information.
        container = await this.client.GetContainerById(container.ID);
        return container.ToNodeEndpoint();
    }

    private async Task<RecoveryAgentEndpoint> CreateAndStartRecoveryAgentContainer(
        string networkName,
        NodeEndpoint nodeEndpoint)
    {
        var containerName = "recovery-agent-nw-" + nodeEndpoint.NodeName;
        var envoyEndpoint = await this.CreateAndStartEnvoyProxyContainer(
            networkName,
            containerName,
            nodeEndpoint);

        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.CcfRecoveryAgentImage(),
            Tag = ImageUtils.CcfRecoveryAgentTag(),
        };
        await this.client.Images.CreateImageAsync(
            imageParams,
            authConfig: null,
            new Progress<JSONMessage>(m => this.logger.LogInformation(m.ToProgressMessage())));

        string hostServiceCertDir =
            DockerClientEx.GetHostServiceCertDirectory("ra", nodeEndpoint.NodeName);

        string hostInsecureVirtualDir =
            DockerClientEx.GetHostInsecureVirtualDirectory("ra", nodeEndpoint.NodeName);
        string insecureVirtualDir =
            DockerClientEx.GetInsecureVirtualDirectory("ra", nodeEndpoint.NodeName);
        Directory.CreateDirectory(insecureVirtualDir);

        // Copy out the test keys/report into the host directory so that it can be mounted into
        // the container.
        WorkspaceDirectories.CopyDirectory(
            Directory.GetCurrentDirectory() + "/insecure-virtual/recovery-agent",
            insecureVirtualDir,
            recursive: true);

        var createContainerParams = new CreateContainerParameters
        {
            Labels = new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfNetworkNameTag,
                    networkName
                },
                {
                    DockerConstants.CcfNetworkTypeTag,
                    "recovery-agent"
                },
                {
                    DockerConstants.CcfNetworkResourceNameTag,
                    nodeEndpoint.NodeName
                }
            },
            Name = containerName,
            Image = $"{imageParams.FromImage}:{imageParams.Tag}",
            Env =
            [
                $"CCF_ENDPOINT={nodeEndpoint.ClientRpcAddress}",
                $"CCF_ENDPOINT_SKIP_TLS_VERIFY=true",
                $"ASPNETCORE_URLS=http://+:{Ports.RecoveryAgentPort}",
                $"SERVICE_CERT_LOCATION={DockerConstants.ServiceCertPemFilePath}",
                $"INSECURE_VIRTUAL_ENVIRONMENT=true"
            ],
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.RecoveryAgentPort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = networkName,
                Devices = new List<DeviceMapping>(), // Allocate these as might be modified later.
                CapAdd = new List<string>(),
                Binds = new List<string>
                {
                    $"{hostServiceCertDir}:{DockerConstants.ServiceFolderMountPath}:ro",
                    $"{hostInsecureVirtualDir}:/app/insecure-virtual:ro"
                },

                // Although traffic will be routed via envoy exposing the HTTP port for easier
                // debugging.
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{Ports.RecoveryAgentPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                    // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    }
                }
            }
        };

        this.logger.LogInformation($"Creating container: {createContainerParams.Name}");
        var container = await this.client.CreateOrGetContainer(createContainerParams);
        await this.client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        return envoyEndpoint.ToRecoveryAgentEndpoint();
    }

    private Task<EnvoyEndpoint> CreateAndStartEnvoyProxyContainer(
        string networkName,
        string envoyDestinationEndpoint,
        NodeEndpoint nodeEndpoint)
    {
        string containerName = "envoy-nw-" + nodeEndpoint.NodeName;
        string serviceCertDir = DockerClientEx.GetServiceCertDirectory("ra", nodeEndpoint.NodeName);
        string hostServiceCertDir =
            DockerClientEx.GetHostServiceCertDirectory("ra", nodeEndpoint.NodeName);

        // Create the scratch directory that gets mounted into the envoy container which then
        // writes out the service cert pem file in this location. The recovery agent container
        // reads this file and serves it out via the /report endpoint.
        Directory.CreateDirectory(serviceCertDir);

        return this.client.CreateEnvoyProxyContainer(
            this.logger,
            envoyDestinationEndpoint,
            Ports.RecoveryAgentPort,
            containerName,
            networkName,
            hostServiceCertDir,
            DockerConstants.CcfNetworkResourceNameTag,
            new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfNetworkNameTag,
                    networkName
                },
                {
                    DockerConstants.CcfNetworkTypeTag,
                    "ccr-proxy"
                },
                {
                    DockerConstants.CcfNetworkResourceNameTag,
                    nodeEndpoint.NodeName
                }
            });
    }
}
