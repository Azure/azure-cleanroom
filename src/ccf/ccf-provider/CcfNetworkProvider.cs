// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using CcfCommon;
using CcfRecoveryProvider;
using Controllers;
using CoseUtils;
using LoadBalancerProvider;
using Microsoft.Extensions.Logging;
using Polly;

namespace CcfProvider;

public class CcfNetworkProvider
{
    private ILogger logger;
    private ICcfNodeProvider nodeProvider;
    private ICcfLoadBalancerProvider lbProvider;
    private CcfClientManager ccfClientManager;
    private HttpClientManager httpClientManager;

    public CcfNetworkProvider(
        ILogger logger,
        ICcfNodeProvider nodeProvider,
        ICcfLoadBalancerProvider lbProvider,
        CcfClientManager ccfClientManager)
    {
        this.logger = logger;
        this.nodeProvider = nodeProvider;
        this.lbProvider = lbProvider;
        this.ccfClientManager = ccfClientManager;
        this.httpClientManager = new(logger);
    }

    private enum DesiredJoinNodeState
    {
        PartOfNetwork,
        PartOfPublicNetwork
    }

    public async Task<CcfNetwork> CreateNetwork(
        string networkName,
        int nodeCount,
        List<InitialMember> initialMembers,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig,
        IProgress<string> progress)
    {
        var startNodeName = networkName + "-0";
        var nodeData = new NodeData
        {
            Name = startNodeName,
            Id = ToId(startNodeName)
        };

        string lbName = "lb-nw-" + networkName;
        var lbFqdn = this.lbProvider.GenerateLoadBalancerFqdn(lbName, networkName, providerConfig);
        progress.Report($"Creating start node '{startNodeName}'...");
        var startNodeEndpoint = await this.nodeProvider.CreateStartNode(
            startNodeName,
            networkName,
            initialMembers,
            nodeLogLevel,
            policyOption,
            nodeData,
            lbFqdn.NodeSanFormat(),
            providerConfig);

        var serviceCertPem = await this.GetServiceCert(
            startNodeName,
            startNodeEndpoint.ClientRpcAddress,
            onRetry: () => this.CheckNodeHealthy(networkName, startNodeName, providerConfig));

        progress.Report($"Waiting for start node to become ready...");
        await this.WaitForStartNodeReady(startNodeEndpoint, serviceCertPem);

        this.logger.LogInformation(
            $"Node {startNodeName} is up at {startNodeEndpoint.ClientRpcAddress}. Now starting " +
            $"remaining {nodeCount - 1} node(s) in join mode.");

        var nodeEndpoints = await this.nodeProvider.GetNodes(networkName, providerConfig);
        int ordinal = int.Parse(nodeEndpoints.OrderBy(
            n => n.NodeName.PadForNaturalNumberOrdering())
            .Last().NodeName.Split("-").Last()) + 1;
        List<NodeEndpoint> joinNodes = await this.CreateJoinNodes(
            networkName,
            nodeCount - 1,
            nodeLogLevel,
            policyOption,
            providerConfig,
            startNodeEndpoint,
            lbFqdn.NodeSanFormat(),
            serviceCertPem,
            DesiredJoinNodeState.PartOfNetwork,
            ordinal,
            progress);

        List<string> servers =
            [startNodeEndpoint.ClientRpcAddress, .. joinNodes.Select(n => n.ClientRpcAddress)];
        List<string> agentServers = [.. (await this.nodeProvider.GetRecoveryAgents(
            networkName,
            providerConfig))!.Select(a =>
            {
                var u = new Uri(a.Endpoint);
                return $"{u.Host}:{u.Port}";
            })];
        progress.Report("Creating load balancer...");
        var lbEndpoint =
            await this.lbProvider.CreateLoadBalancer(
                lbName,
                networkName,
                servers,
                agentServers,
                providerConfig);

        progress.Report($"Waiting for load balancer to become ready...");
        await this.WaitForLoadBalancerReady(lbEndpoint, serviceCertPem);

        progress.Report($"CCF endpoint is up at: {lbEndpoint.Endpoint}.");
        this.logger.LogInformation($"CCF endpoint is up at: {lbEndpoint.Endpoint}.");
        return new CcfNetwork
        {
            Name = networkName,
            InfraType = this.nodeProvider.InfraType.ToString(),
            NodeCount = nodeCount,
            Endpoint = lbEndpoint.Endpoint,
            Nodes = servers
        };
    }

    public async Task DeleteNetwork(
        string networkName,
        DeleteOption deleteOption,
        JsonObject? providerConfig)
    {
        await this.nodeProvider.DeleteNodes(networkName, deleteOption, providerConfig);
        await this.lbProvider.DeleteLoadBalancer(networkName, providerConfig);
    }

    public async Task<CcfNetwork?> GetNetwork(string networkName, JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.TryGetLoadBalancerEndpoint(networkName, providerConfig);
        if (lbEndpoint != null)
        {
            List<NodeEndpoint> nodeEndpoints =
                await this.nodeProvider.GetNodes(networkName, providerConfig);
            return new CcfNetwork
            {
                Name = networkName,
                InfraType = this.nodeProvider.InfraType.ToString(),
                NodeCount = nodeEndpoints.Count,
                Endpoint = lbEndpoint.Endpoint,
                Nodes = nodeEndpoints.ConvertAll(n => n.ClientRpcAddress)
            };
        }

        return null;
    }

    public async Task<CcfNetworkHealth> GetNetworkHealth(
        string networkName,
        JsonObject? providerConfig)
    {
        List<Task> tasks = new();
        var nhTask = this.nodeProvider.GetNodesHealth(networkName, providerConfig);
        tasks.Add(nhTask);
        var lbTask = this.lbProvider.GetLoadBalancerHealth(networkName, providerConfig);
        tasks.Add(lbTask);
        await Task.WhenAll(tasks);
        List<NodeHealth> nodesHealth = await nhTask;
        LoadBalancerHealth lbHealth = await lbTask;

        return new CcfNetworkHealth
        {
            LoadBalancerHealth = lbHealth,
            NodeHealth = nodesHealth
        };
    }

    public async Task<CcfNetwork> UpdateNetwork(
        string networkName,
        int nodeCount,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        if (nodeCount < 1)
        {
            throw new ArgumentException($"New node count value cannot be less than 1.");
        }

        // Before proceeding further check that signing cert/key that would be required to submit
        // any proposal are configured.
        await this.ccfClientManager.CheckSigningConfig();

        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var lbFqdn = this.lbProvider.GenerateLoadBalancerFqdn(
            lbEndpoint.Name,
            networkName,
            providerConfig);

        // Pick primary per CCF network as the target node to use for joining/removing nodes from
        // the network.
        (NodeEndpoint primaryNodeEndpoint, string primaryNodeId, string serviceCertPem) =
            await this.GetPrimaryNodeEndpoint(networkName, providerConfig, lbEndpoint);

        this.logger.LogInformation(
            $"Current primary node: " +
            $"{primaryNodeEndpoint.NodeName}, endpoint: {primaryNodeEndpoint.ClientRpcAddress}");

        var primaryClient = this.GetOrAddServiceClient(
            primaryNodeEndpoint,
            serviceCertPem,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));

        // First clean up any nodes already in Retired state so that we don't count them towards
        // node addition/removal as these are stale and would have been left behind from a
        // previous failed/aborted attempt.
        bool retiredAny =
            await this.CleanupRetiredNodes(networkName, providerConfig, primaryClient);

        // Populate node endpoints after cleanup as the above cleanup could have reduced the nodes
        // reported by the infra provider.
        var nodeEndpoints = await this.nodeProvider.GetNodes(networkName, providerConfig);
        bool addedAny = false;
        bool removedAny = false;
        if (nodeCount > nodeEndpoints.Count)
        {
            this.logger.LogInformation("Going to add nodes.");

            // Add nodes.
            // TODO (gsinha): Above check does not handle the situation where nodes got orphaned
            // during the creation process and were never reported on the network but are
            // enumerated by the infra provider via GetNodes. We should not be counting them as
            // added nodes but the ordinal calculation below needs to account for their name to
            // calculate the next highest ordinal.
            // Similarly nodes that need replacement (per GetNodeHealth) should not be counted
            // as added nodes.
            int ordinal = int.Parse(nodeEndpoints.OrderBy(
                n => n.NodeName.PadForNaturalNumberOrdering())
                .Last().NodeName.Split("-").Last()) + 1;
            int numNodesToCreate = nodeCount - nodeEndpoints.Count;
            addedAny = await this.AddNodes(
                networkName,
                providerConfig,
                numNodesToCreate,
                primaryNodeEndpoint,
                lbFqdn.NodeSanFormat(),
                serviceCertPem,
                nodeLogLevel,
                policyOption,
                ordinal);
        }
        else if (nodeCount < nodeEndpoints.Count)
        {
            this.logger.LogInformation("Going to remove nodes.");

            // Remove nodes.
            int numNodesToRemove = nodeEndpoints.Count - nodeCount;
            List<NetworkNode> nodesToRemove = await this.PickNodesToRemove(
                networkName,
                providerConfig,
                primaryClient,
                primaryNodeId,
                numNodesToRemove);
            removedAny =
                await this.RemoveNodes(networkName, providerConfig, nodesToRemove, primaryClient);
        }

        if (addedAny || removedAny)
        {
            nodeEndpoints = await this.nodeProvider.GetNodes(networkName, providerConfig);
        }

        if (nodeCount == nodeEndpoints.Count)
        {
            this.logger.LogInformation($"Checking if any nodes need to be replaced.");

            var nodesHealth = await this.nodeProvider.GetNodesHealth(networkName, providerConfig);
            var unhealthyNodes = nodeEndpoints.Where(
                n => nodesHealth.Any(nh => nh.Name == n.NodeName &&
                    nh.Status == nameof(NodeStatus.NeedsReplacement)))
                .ToList();
            if (unhealthyNodes.Any())
            {
                var nodes =
                    (await primaryClient.GetFromJsonAsync<NetworkNodeList>("/node/network/nodes"))!
                    .Nodes;
                this.logger.LogInformation(
                    $"Current nodes: {JsonSerializer.Serialize(nodes, Utils.Options)}.");

                List<NetworkNode> nodesToRemove =
                    nodes
                    .Where(n => unhealthyNodes.Any(nh => ToId(nh.NodeName) == ToId(n.NodeData.Name)))
                    .ToList();

                this.logger.LogInformation(
                    $"Need to replace {unhealthyNodes.Count} nodes out of {nodeCount} that are " +
                    $"reporting status as needing replacement. Nodes health: " +
                    $"{JsonSerializer.Serialize(nodesHealth, Utils.Options)}");

                var unexpectedPrimary = nodesToRemove.Find(n => n.Primary);
                var currentPrimary = nodesToRemove.Find(n => n.NodeId == primaryNodeId);
                if (unexpectedPrimary != null)
                {
                    // Primary has shifted and we picked a node that reported itself as primary but
                    // the infra provider has marked it as needing replacement. Let the primary
                    // stabilize as most likely a new primary will get elected. So do nothing.
                    this.logger.LogWarning(
                        $"'{unexpectedPrimary.NodeId}' is reporting itself as Primary but was " +
                        $"the infra provider is indicating that the node be replaced. Let the " +
                        $"primary stabilize as most likely a new primary will get elected. " +
                        $"Try again later.");
                }
                else if (currentPrimary != null)
                {
                    // Primary has shifted and we picked a node that reported itself as primary but
                    // the infra provider has marked it as needing replacement. Let the primary
                    // stabilize as most likely a new primary will get elected. So do nothing.
                    this.logger.LogWarning(
                        $"'{currentPrimary.NodeId}' was considered primary but " +
                        $"the infra provider is indicating that the node be replaced. Let the " +
                        $"primary stabilize as most likely a new primary will get elected. " +
                        $"Try again later.");
                }
                else
                {
                    // We first add a new set of nodes before removing. In failure situations
                    // we might have more nodes lying around if removal of the unhealthy nodes
                    // fail. This would get cleaned up in the next attempt to update the node
                    // count (or once we have a health watcher that periodically reconciles
                    // with the desired node count.
                    int ordinal = int.Parse(nodeEndpoints.OrderBy(
                        n => n.NodeName.PadForNaturalNumberOrdering())
                        .Last().NodeName.Split("-").Last()) + 1;
                    int numNodesToCreate = unhealthyNodes.Count;
                    addedAny = await this.AddNodes(
                        networkName,
                        providerConfig,
                        numNodesToCreate,
                        primaryNodeEndpoint,
                        lbFqdn.NodeSanFormat(),
                        serviceCertPem,
                        nodeLogLevel,
                        policyOption,
                        ordinal);

                    removedAny = await this.RemoveNodes(
                        networkName,
                        providerConfig,
                        nodesToRemove,
                        primaryClient);
                }
            }
            else
            {
                this.logger.LogInformation(
                    $"Not replacing any nodes as input nodeCount {nodeCount} matches number " +
                    $"of healthy nodes reported by the infra provider.");
            }
        }

        if (retiredAny || addedAny || removedAny)
        {
            var nodesHealth = await this.nodeProvider.GetNodesHealth(networkName, providerConfig);
            nodeEndpoints = await this.nodeProvider.GetNodes(networkName, providerConfig);
            var availableNodeEndpoints = nodeEndpoints.Where(
                n => !nodesHealth.Any(nh => nh.Name == n.NodeName &&
                nh.Status == nameof(NodeStatus.NeedsReplacement)));
            List<string> servers = new(availableNodeEndpoints.Select(n => n.ClientRpcAddress));
            List<string> agentServers = [.. (await this.nodeProvider.GetRecoveryAgents(
                networkName,
                providerConfig))!
                .Where(a => availableNodeEndpoints.Any(n => n.NodeName == a.Name))
                .Select(a =>
            {
                var u = new Uri(a.Endpoint);
                return $"{u.Host}:{u.Port}";
            })];
            this.logger.LogInformation(
                $"Updating LB with servers: {JsonSerializer.Serialize(servers)}, " +
                $"agent servers: {JsonSerializer.Serialize(agentServers)}.");
            lbEndpoint = await this.lbProvider.UpdateLoadBalancer(
                lbEndpoint.Name,
                networkName,
                servers,
                agentServers,
                providerConfig);

            await this.WaitForLoadBalancerReady(lbEndpoint, serviceCertPem);
        }

        this.logger.LogInformation($"CCF endpoint is up at: {lbEndpoint.Endpoint}.");
        return new CcfNetwork
        {
            Name = networkName,
            InfraType = this.nodeProvider.InfraType.ToString(),
            NodeCount = nodeEndpoints.Count,
            Endpoint = lbEndpoint.Endpoint,
            Nodes = nodeEndpoints.ConvertAll(n => n.ClientRpcAddress)
        };
    }

    public async Task<CcfNetwork> RecoverPublicNetwork(
        string targetNetworkName,
        string networkToRecoverName,
        int nodeCount,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        string previousServiceCertificate,
        JsonObject? providerConfig,
        IProgress<string> progress)
    {
        List<string> recoveryNodeNames =
            await this.nodeProvider.GetCandidateRecoveryNodes(networkToRecoverName, providerConfig);
        List<Task> recoverTasks = new();
        ConcurrentBag<
            (NetworkNode node, long lastSignedSeqNo, NodeEndpoint ep, string serviceCertPem)>
            recoverNodeEndpoints = new();
        string lbName = "lb-nw-" + targetNetworkName;
        var lbFqdn = this.lbProvider.GenerateLoadBalancerFqdn(
            lbName,
            targetNetworkName,
            providerConfig);
        foreach (var recoveryNode in recoveryNodeNames)
        {
            recoverTasks.Add(Task.Run(async () =>
            {
                var recoverNodeName = recoveryNode;
                var nodeData = new NodeData
                {
                    Name = recoverNodeName,
                    Id = ToId(recoverNodeName)
                };

                progress.Report($"Creating recover node '{recoverNodeName}'...");
                var recoverNodeEndpoint = await this.nodeProvider.CreateRecoverNode(
                    recoverNodeName,
                    targetNetworkName,
                    networkToRecoverName,
                    previousServiceCertificate,
                    nodeLogLevel,
                    policyOption,
                    nodeData,
                    lbFqdn.NodeSanFormat(),
                    providerConfig);

                var serviceCertPem = await this.GetServiceCert(
                    recoverNodeName,
                    recoverNodeEndpoint.ClientRpcAddress,
                    onRetry: () => this.CheckNodeHealthy(targetNetworkName, recoverNodeName, providerConfig));

                progress.Report($"Waiting for public network recovery on node " +
                    $"'{recoverNodeName}'...");
                (NetworkNode node, long lastSignedSeqNo) = await this.WaitForRecoverNodeReady(
                    recoverNodeEndpoint,
                    serviceCertPem,
                    DesiredJoinNodeState.PartOfPublicNetwork);

                this.logger.LogInformation(
                    $"Node {recoverNodeName} is up at {recoverNodeEndpoint.ClientRpcAddress} " +
                    $"with lastSignedSeqNo: {lastSignedSeqNo}.");
                (NetworkNode, long, NodeEndpoint, string) candidate =
                (node, lastSignedSeqNo, recoverNodeEndpoint, serviceCertPem);
                recoverNodeEndpoints.Add(candidate);
            }));
        }

        await Task.WhenAll(recoverTasks);
        var nodesToCheck = recoverNodeEndpoints.OrderByDescending(x => x.lastSignedSeqNo);
        var nodeToUse = nodesToCheck.First();
        var nodesToShutdown = nodesToCheck.Skip(1);

        if (nodesToShutdown.Any())
        {
            this.logger.LogInformation(
                $"Node {nodeToUse.ep.NodeName} up at {nodeToUse.ep.ClientRpcAddress} with " +
                $"lastSignedSeqNo: {nodeToUse.lastSignedSeqNo} will be used for recovery. " +
                $"Shutting down other {nodesToShutdown.Count()} nodes.");
            List<Task> deleteTasks = new();
            if (nodesToShutdown.Any())
            {
                progress.Report($"Using node {nodeToUse.ep.NodeName} for recovery. " +
                    $"Removing other recover nodes.");
            }

            foreach (var (node, lastSignedSeqNo, ep, serviceCertPem) in nodesToShutdown)
            {
                deleteTasks.Add(Task.Run(async () =>
                {
                    progress.Report($"Removing node {node.NodeData.Name}...");
                    await this.nodeProvider.DeleteNode(
                        targetNetworkName,
                        node.NodeData.Name,
                        node.NodeData,
                        providerConfig);
                }));
            }

            await Task.WhenAll(deleteTasks);
        }

        this.logger.LogInformation(
            $"Node {nodeToUse.ep.NodeName} up at {nodeToUse.ep.ClientRpcAddress} with " +
            $"lastSignedSeqNo: {nodeToUse.lastSignedSeqNo} will be used for recovery. " +
            $"Now starting " +
            $"remaining {nodeCount - 1} node(s) in join mode.");

        var nodeEndpoints = await this.nodeProvider.GetNodes(targetNetworkName, providerConfig);
        int ordinal = int.Parse(nodeEndpoints.OrderBy(
            n => n.NodeName.PadForNaturalNumberOrdering())
            .Last().NodeName.Split("-").Last()) + 1;
        List<NodeEndpoint> joinNodes = await this.CreateJoinNodes(
            targetNetworkName,
            nodeCount - 1,
            nodeLogLevel,
            policyOption,
            providerConfig,
            nodeToUse.ep,
            lbFqdn.NodeSanFormat(),
            nodeToUse.serviceCertPem,
            DesiredJoinNodeState.PartOfPublicNetwork,
            ordinal,
            progress);

        List<string> servers =
            [nodeToUse.ep.ClientRpcAddress, .. joinNodes.Select(n => n.ClientRpcAddress)];
        List<string> agentServers = [.. (await this.nodeProvider.GetRecoveryAgents(
            targetNetworkName,
            providerConfig))!.Select(a =>
            {
                var u = new Uri(a.Endpoint);
                return $"{u.Host}:{u.Port}";
            })];

        progress.Report("Creating load balancer...");
        var lbEndpoint = await this.lbProvider.CreateLoadBalancer(
            lbName,
            targetNetworkName,
            servers,
            agentServers,
            providerConfig);

        progress.Report($"Waiting for load balancer to become ready...");
        await this.WaitForLoadBalancerReady(lbEndpoint, nodeToUse.serviceCertPem);

        this.logger.LogInformation($"CCF endpoint is up at: {lbEndpoint.Endpoint}.");
        return new CcfNetwork
        {
            Name = targetNetworkName,
            InfraType = this.nodeProvider.InfraType.ToString(),
            NodeCount = nodeCount,
            Endpoint = lbEndpoint.Endpoint,
            Nodes = servers
        };
    }

    public async Task<JsonObject> TriggerSnapshot(string networkName, JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "trigger_snapshot"
                }
            }
        };
        return await this.CreateProposal(ccfClient, proposalContent);
    }

    public async Task<JsonObject> TransitionToOpen(
        string networkName,
        string? previousServiceCertificate,
        JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var args = new JsonObject
        {
            ["next_service_identity"] = serviceCert
        };

        if (!string.IsNullOrEmpty(previousServiceCertificate))
        {
            args["previous_service_identity"] = previousServiceCertificate;
        }

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "transition_service_to_open",
                    ["args"] = args
                }
            }
        };

        return await this.CreateProposal(ccfClient, proposalContent);
    }

    public async Task<JsonObject> GetReport(string networkName, JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var response = (await ccfClient.GetFromJsonAsync<QuotesList>("/node/quotes"))!;
        var reportsArray = new JsonArray();
        foreach (var quote in response.Quotes)
        {
            if (quote.Format == "Insecure_Virtual")
            {
                var hostData = JsonSerializer.Deserialize<JsonObject>(
                    Convert.FromBase64String(quote.Raw))!["host_data"];
                reportsArray.Add(new JsonObject
                {
                    ["hostData"] = hostData?.ToString(),
                    ["raw"] = quote.Raw,
                    ["endorsements"] = quote.Endorsements,
                    ["nodeId"] = quote.NodeId,
                    ["format"] = quote.Format,
                    ["verified"] = true
                });
            }
            else
            {
                var report = SnpReport.VerifySnpAttestation(
                    quote.Raw,
                    quote.Endorsements,
                    uvmEndorsements: null);
                reportsArray.Add(new JsonObject
                {
                    ["hostData"] = report.HostData.ToLower(),
                    ["raw"] = quote.Raw,
                    ["endorsements"] = quote.Endorsements,
                    ["nodeId"] = quote.NodeId,
                    ["format"] = quote.Format,
                    ["verified"] = true
                });
            }
        }

        return new JsonObject
        {
            ["reports"] = reportsArray
        };
    }

    public async Task<JsonObject> AddSnpHostData(
        string networkName,
        string hostData,
        string? securityPolicyBase64,
        JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var args = new JsonObject
        {
            ["host_data"] = hostData
        };

        var securityPolicy = string.Empty;
        if (!string.IsNullOrEmpty(securityPolicyBase64))
        {
            securityPolicy = Encoding.UTF8.GetString(
                Convert.FromBase64String(securityPolicyBase64));
        }

        args["security_policy"] = securityPolicy;
        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "add_snp_host_data",
                    ["args"] = args
                }
            }
        };

        return await this.CreateProposal(ccfClient, proposalContent);
    }

    public async Task<JsonObject> RemoveSnpHostData(
        string networkName,
        string hostData,
        JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var args = new JsonObject
        {
            ["host_data"] = hostData
        };

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "remove_snp_host_data",
                    ["args"] = args
                }
            }
        };

        return await this.CreateProposal(ccfClient, proposalContent);
    }

    public async Task<JsonObject> GetJoinPolicy(string networkName, JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var joinPolicy = (await ccfClient.GetFromJsonAsync<JsonObject>(
            "gov/service/join-policy" +
            $"?api-version={this.ccfClientManager.GetGovApiVersion()}"))!;
        return joinPolicy;
    }

    public async Task<JsonObject> GenerateSecurityPolicy(SecurityPolicyCreationOption policyOption)
    {
        return await this.nodeProvider.GenerateSecurityPolicy(policyOption);
    }

    public async Task<JsonObject> GenerateJoinPolicy(SecurityPolicyCreationOption policyOption)
    {
        return await this.nodeProvider.GenerateJoinPolicy(policyOption);
    }

    public async Task<JsonObject> GenerateJoinPolicy(string networkName, JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var joinPolicy = (await ccfClient.GetFromJsonAsync<Ccf.JoinPolicyInfo>(
            "gov/service/join-policy" +
            $"?api-version={this.ccfClientManager.GetGovApiVersion()}"))!;

        var hostDataArray = new JsonArray();
        foreach (var item in joinPolicy.Snp.HostData)
        {
            hostDataArray.Add(item.Key);
        }

        var policy = new JsonObject
        {
            ["snp"] = new JsonObject
            {
                ["hostData"] = hostDataArray
            }
        };

        return policy;
    }

    public async Task<JsonObject> SetRecoveryThreshold(
        string networkName,
        int recoveryThreshold,
        JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);
        var args = new JsonObject
        {
            ["recovery_threshold"] = recoveryThreshold
        };

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "set_recovery_threshold",
                    ["args"] = args
                }
            }
        };

        return await this.CreateProposal(ccfClient, proposalContent);
    }

    public async Task<JsonObject> SubmitRecoveryShare(
        string networkName,
        CoseSignKey coseSignKey,
        RSA rsaEncKey,
        JsonObject? providerConfig)
    {
        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);

        using var cert = X509Certificate2.CreateFromPem(coseSignKey.Certificate);
        var memberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();
        var response = await ccfClient.GetFromJsonAsync<JsonObject>(
            $"/gov/recovery/encrypted-shares/{memberId}" +
            $"?api-version={this.ccfClientManager.GetGovApiVersion()}");
        var encryptedShare = response!["encryptedShare"]!.ToString();

        byte[] wrappedValue = Convert.FromBase64String(encryptedShare);
        var decryptedShare = rsaEncKey.Decrypt(wrappedValue, RSAEncryptionPadding.OaepSHA256);

        var wrappedDecryptedShare = Convert.ToBase64String(decryptedShare);
        JsonObject content = new()
        {
            ["share"] = wrappedDecryptedShare
        };

        return await this.SubmitRecoveryShare(
            ccfClient,
            memberId,
            coseSignKey,
            content);
    }

    public async Task<CcfNetwork> RecoverNetwork(
        string networkName,
        int nodeCount,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        string previousServiceCertificate,
        RSA rsaEncKey,
        JsonObject? providerConfig,
        IProgress<string> progress)
    {
        // Before proceeding further check that signing cert/key that would be required to submit
        // any proposal are configured.
        await this.ccfClientManager.CheckSigningConfig();

        // This is an opinionated recovery flow using the supplied encryption key
        // that orchestrates the below sequence:
        // - Removes all node instances of the existing network while retaining storage
        //   (if recovery failed mid way then this cleans up the nodes on the next attempt)
        // - Recover the public network
        // - Transition service to open
        // - Submit a recovery share assuming the operator is the only 1 recovery member required
        //   for recovery.
        // - Wait for recovery node to become PartOfNetwork.
        // - Remove any retired (pre-DR) nodes.
        progress.Report("Removing stale node(s)...");
        await this.DeleteNetwork(networkName, DeleteOption.RetainStorage, providerConfig);

        var recoveryNetwork = await this.RecoverPublicNetwork(
            networkName,
            networkName,
            nodeCount,
            nodeLogLevel,
            policyOption,
            previousServiceCertificate,
            providerConfig,
            progress);

        await this.TransitionToOpen(networkName, previousServiceCertificate, providerConfig);

        var signingConfig = await this.ccfClientManager.GetSigningConfig();

        JsonObject response = await this.SubmitRecoveryShare(
            networkName,
            signingConfig.CoseSignKey,
            rsaEncKey,
            providerConfig);

        if (!response["message"]!.ToString().Contains("End of recovery procedure initiated"))
        {
            throw new Exception(
                $"Assuming only 1 member required for recovery hence expecting end of recovery " +
                $"procedure but got: " +
                $"{JsonSerializer.Serialize(response, Utils.Options)}");
        }

        var recoverNodeEndpoint = new NodeEndpoint
        {
            NodeName = $"{networkName}-0",
            ClientRpcAddress = recoveryNetwork.Nodes[0]
        };

        var serviceCertPem = await this.GetServiceCert(
            recoverNodeEndpoint.NodeName,
            recoverNodeEndpoint.ClientRpcAddress,
            onRetry: () => this.CheckNodeHealthy(
                networkName,
                recoverNodeEndpoint.NodeName,
                providerConfig));

        progress.Report($"Waiting for network recovery completion...");
        await this.WaitForRecoverNodeReady(
            recoverNodeEndpoint,
            serviceCertPem,
            DesiredJoinNodeState.PartOfNetwork);

        var primaryClient = this.GetOrAddServiceClient(
            recoverNodeEndpoint,
            serviceCertPem,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));
        await this.CleanupRetiredNodesPostRecovery(
            networkName,
            providerConfig,
            primaryClient);

        progress.Report($"CCF endpoint is up at: {recoveryNetwork.Endpoint}.");
        return recoveryNetwork;
    }

    public async Task<CcfNetwork> RecoverNetwork(
        string networkName,
        int nodeCount,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        string previousServiceCertificate,
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider,
        string confidentialRecoveryMemberName,
        CcfRecoveryService recoveryService,
        JsonObject? providerConfig,
        IProgress<string> progress)
    {
        // Before proceeding further check that signing cert/key that would be required to submit
        // any proposal are configured.
        await this.ccfClientManager.CheckSigningConfig();

        var agentConfig = new AgentConfig
        {
            RecoveryService = new RecoveryServiceConfig
            {
                Endpoint = recoveryService.Endpoint,
                ServiceCert = recoveryService.ServiceCert
            }
        };

        // This is an opinionated recovery flow using the confidential recovery service
        // that orchestrates the below sequence:
        // - Removes all node instances of the existing network while retaining storage
        //   (if recovery failed mid way then this cleans up the nodes on the next attempt)
        // - Recover the public network
        // - Transition service to open
        // - Request the confidential recovery service to submit the recovery share assuming the
        //   confidential recoverer member is the only recovery member required
        //   for recovery.
        // - Wait for recovery node to become PartOfNetwork.
        // - Remove any retired (pre-DR) nodes.
        progress.Report("Removing stale node(s)...");
        await this.DeleteNetwork(networkName, DeleteOption.RetainStorage, providerConfig);

        var recoveryNetwork = await this.RecoverPublicNetwork(
            networkName,
            networkName,
            nodeCount,
            nodeLogLevel,
            policyOption,
            previousServiceCertificate,
            providerConfig,
            progress);

        await this.TransitionToOpen(networkName, previousServiceCertificate, providerConfig);

        JsonObject response = await recoveryAgentProvider.SubmitRecoveryShare(
            networkName,
            confidentialRecoveryMemberName,
            agentConfig,
            providerConfig);

        var message = response["message"]!.ToString();
        if (!message.Contains("Full recovery key successfully submitted") ||
            !message.Contains("End of recovery procedure initiated"))
        {
            throw new Exception(
                $"Assuming only full recovery key required for recovery hence expecting end of " +
                $"recovery procedure but got: {JsonSerializer.Serialize(response, Utils.Options)}");
        }

        var recoverNodeEndpoint = new NodeEndpoint
        {
            NodeName = $"{networkName}-0",
            ClientRpcAddress = recoveryNetwork.Nodes[0]
        };

        var serviceCertPem = await this.GetServiceCert(
            recoverNodeEndpoint.NodeName,
            recoverNodeEndpoint.ClientRpcAddress,
            onRetry: () => this.CheckNodeHealthy(
                networkName,
                recoverNodeEndpoint.NodeName,
                providerConfig));

        progress.Report($"Waiting for network recovery completion...");
        await this.WaitForRecoverNodeReady(
            recoverNodeEndpoint,
            serviceCertPem,
            DesiredJoinNodeState.PartOfNetwork);

        var primaryClient = this.GetOrAddServiceClient(
            recoverNodeEndpoint,
            serviceCertPem,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));
        await this.CleanupRetiredNodesPostRecovery(
            networkName,
            providerConfig,
            primaryClient);

        progress.Report($"CCF endpoint is up at: {recoveryNetwork.Endpoint}.");
        return recoveryNetwork;
    }

    public async Task ConfigureConfidentialRecovery(
        string networkName,
        string confidentialRecoveryMemberName,
        CcfRecoveryService recoveryService,
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider,
        JsonObject? providerConfig)
    {
        // Before proceeding further check that signing cert/key that would be required to submit
        // any proposal are configured.
        await this.ccfClientManager.CheckSigningConfig();

        var agentConfig = new AgentConfig
        {
            RecoveryService = new RecoveryServiceConfig
            {
                Endpoint = recoveryService.Endpoint,
                ServiceCert = recoveryService.ServiceCert
            }
        };

        var lbEndpoint =
            await this.lbProvider.GetLoadBalancerEndpoint(networkName, providerConfig);
        var serviceCert = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var ccfClient = await this.ccfClientManager.GetGovClient(lbEndpoint.Endpoint, serviceCert);

        // This is an opinionated configure flow using the confidential recovery service
        // that orchestrates the below sequence:
        // - Generates a recovery member in the recovery service
        // - Checks that no other recovery member besides a confidential recovery member is present
        // - Adds the recovery member as the recovery operator into the consortium
        // - Requests recovery service to activate its membership in the consortium
        // - Sets recovery threshold to 1 so only the confidential recovery member is required
        //   for recovery
        JsonObject member = await recoveryAgentProvider.GenerateRecoveryMember(
            networkName,
            confidentialRecoveryMemberName,
            agentConfig,
            providerConfig);

        var signingCert = member["signingCert"]!.ToString();
        var encryptionPublicKey = member["encryptionPublicKey"]!.ToString();

        // Serialize and Deserialize to avoid "The node already has a parent." if AsObject() is
        // used directly.
        var recoveryServiceData = JsonSerializer.Deserialize<JsonObject>(
            JsonSerializer.Serialize(member["recoveryService"]!.AsObject()))!;
        using var cert = X509Certificate2.CreateFromPem(signingCert);
        var memberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();

        await this.AddAndAcceptRecoveryOperator(
            ccfClient,
            networkName,
            confidentialRecoveryMemberName,
            signingCert,
            encryptionPublicKey,
            recoveryServiceData,
            providerConfig);

        await recoveryAgentProvider.ActivateRecoveryMember(
            networkName,
            confidentialRecoveryMemberName,
            agentConfig,
            providerConfig);
    }

    private static string ToId(string nodeName)
    {
        return nodeName.ToLower();
    }

    private async Task WaitForStartNodeReady(
        NodeEndpoint startNodeEndpoint,
        string serviceCertPem)
    {
        // No retry policy is specified as retries are handled in the loop below.
        var nodeSelfSignedCertPem = await this.GetNodeSelfSignedCert(startNodeEndpoint);
        var client = this.GetOrAddNodeClient(
            startNodeEndpoint,
            HttpRetries.Policies.NoRetries,
            serviceCertPem,
            nodeSelfSignedCertPem);

        TimeSpan readyTimeout = TimeSpan.FromSeconds(60);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var response = await client.GetAsync("/node/ready/gov");
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                break;
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException("Hit timeout waiting for start node to become ready");
            }

            this.logger.LogInformation(
                $"Waiting for {startNodeEndpoint.ClientRpcAddress}/node/ready/gov to report " +
                $"204. Current status: {response.StatusCode}");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<List<NodeEndpoint>> CreateJoinNodes(
        string networkName,
        int numNodesToCreate,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig,
        NodeEndpoint targetNodeEndpoint,
        List<string> san,
        string serviceCertPem,
        DesiredJoinNodeState desiredJoinNodeState,
        int startOrdinal,
        IProgress<string> progress)
    {
        List<Task> joinTasks = new();
        ConcurrentBag<NodeEndpoint> joinNodes = new();
        while (numNodesToCreate != 0)
        {
            int ordinal = startOrdinal;
            joinTasks.Add(Task.Run(async () =>
            {
                var joinNodeName = networkName + "-" + ordinal;
                var nodeData = new NodeData
                {
                    Name = joinNodeName,
                    Id = ToId(joinNodeName)
                };

                progress.Report($"Creating join node '{joinNodeName}'...");
                var joinNodeEndpoint = await this.nodeProvider.CreateJoinNode(
                    joinNodeName,
                    networkName,
                    serviceCertPem,
                    targetNodeEndpoint.NodeName,
                    targetNodeEndpoint.ClientRpcAddress,
                    nodeLogLevel,
                    policyOption,
                    nodeData,
                    san,
                    providerConfig);

                // Check before waiting for the node to become ready.
                await this.CheckNodeHealthy(networkName, joinNodeName, providerConfig);

                joinNodes.Add(joinNodeEndpoint);
                progress.Report($"Waiting for join node '{joinNodeName}' to become ready...");
                await this.WaitForJoinNodeReady(
                    networkName,
                    providerConfig,
                    targetNodeEndpoint,
                    joinNodeEndpoint,
                    serviceCertPem,
                    desiredJoinNodeState);

                this.logger.LogInformation(
                    $"Node {joinNodeName} is up at {joinNodeEndpoint.ClientRpcAddress}.");
            }));

            numNodesToCreate--;
            startOrdinal++;
        }

        await Task.WhenAll(joinTasks);

        return joinNodes.ToList();
    }

    private async Task WaitForJoinNodeReady(
        string networkName,
        JsonObject? providerConfig,
        NodeEndpoint targetNodeEndpoint,
        NodeEndpoint joinNodeEndpoint,
        string serviceCertPem,
        DesiredJoinNodeState desiredState)
    {
        var serviceClient = this.GetOrAddServiceClient(
            targetNodeEndpoint,
            serviceCertPem,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));

        // For nodes joining in network open state we need to transition the node to trusted
        // before the node can finish joining successfully.
        // TODO (gsinha): Add retries around GetFromJsonAsync transient failure.
        var networkState = (await serviceClient.GetFromJsonAsync<JsonObject>("/node/network"))!;
        if (networkState["service_status"]!.ToString() == "Open")
        {
            JsonObject nodeState =
                await this.WaitForNodeToAppearOnNetwork(
                    serviceClient,
                    joinNodeEndpoint.NodeName,
                    onRetry: () => this.CheckNodeHealthy(
                        networkName,
                        joinNodeEndpoint.NodeName,
                        providerConfig));
            var status = nodeState["status"]!.ToString();
            if (status == "Pending")
            {
#pragma warning disable MEN002 // Line is too long
                // At times node to node communication between the new and the primary takes
                // a while to get established due to DNS resolve/caching issues. This shows up
                // as the create proposal transcation commit taking time. So set a higher timeout
                // to give a chance to communication to get established.
                //# Node 1 added to Raft config
                // 2024-11-07T08:17:28.355534Z -0.017 0   [info ] ../src/node/channels.h:828           | Initiating node channel with n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701].
                // 2024-11-07T08:17:28.355852Z        100 [debug] ../src/host/node_connections.h:458   | Added node connection with n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701] (foo-1.westeurope.azurecontainer.io:8081)
                // 2024-11-07T08:17:28.355863Z        100 [debug] ../src/host/node_connections.h:434   | node send to n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701] [1208]
                // 2024-11-07T08:17:28.355868Z -0.018 0   [info ] ../src/consensus/aft/raft.h:2567     | Added raft node n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701] (foo-1.westeurope.azurecontainer.io:8081)
                //# Still unable to connect to Node 1
                // 2024-11-07T08:17:30.358161Z -0.004 0   [info ] ../src/node/channels.h:828           | Initiating node channel with n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701].
                // 2024-11-07T08:17:30.358490Z        100 [debug] ../src/host/node_connections.h:434   | node send to n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701] [1208]
                // 2024-11-07T08:17:30.382290Z        100 [debug] ../src/host/tcp.h:699                | uv_tcp_connect async retry: connection timed out
                // 2024-11-07T08:17:30.382401Z        100 [info ] ../src/host/tcp.h:536                | Unable to connect: all resolved addresses failed: foo-1.westeurope.azurecontainer.io:8081
                // 2024-11-07T08:17:30.382412Z        100 [debug] ../src/host/node_connections.h:227   | Disconnecting outgoing connection with n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701]: connect failed
                // 2024-11-07T08:17:30.382454Z        100 [debug] ../src/host/node_connections.h:472   | Removed node connection with n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701]
                //...
                //# Eventually succeed in connecting to Node 1
                // 2024-11-07T08:18:32.142617Z -0.004 0   [info ] ../src/node/channels.h:828           | Initiating node channel with n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701].
                // 2024-11-07T08:18:32.146380Z        100 [debug] ../src/host/node_connections.h:458   | Added node connection with n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701] (foo-1.westeurope.azurecontainer.io:8081)
                // 2024-11-07T08:18:32.146430Z        100 [debug] ../src/host/node_connections.h:434   | node send to n[d267d70732038c31038eabfc093b63745520560ec0969160595673ad95b05701] [1208]
                // 2024-11-07T08:18:32.150568Z        100 [info ] ../src/host/socket.h:53              | TCP Node Outgoing connected
                //# Commit advances
                // 2024-11-07T08:18:32.926155Z        100 [debug] ../src/host/ledger.h:1435            | Ledger commit: 133/133
#pragma warning restore MEN002 // Line is too long
                var timeout = TimeSpan.FromSeconds(180);
                await TransitionNodeToTrusted(
                    serviceClient,
                    nodeState["node_id"]!.ToString(),
                    timeout);
            }
        }

        // Do a health check as part of retries as in case the join node fails to start then the
        // https endpoint won't respond and there would be no point retrying.
        var selfSignedCertPem = await this.GetNodeSelfSignedCert(
            joinNodeEndpoint,
            onRetry: () => this.CheckNodeHealthy(
                networkName,
                joinNodeEndpoint.NodeName,
                providerConfig));

        // No retry policy is specified as retries are handled in the loop below.
        var client = this.GetOrAddNodeClient(
            joinNodeEndpoint,
            HttpRetries.Policies.NoRetries,
            serviceCertPem,
            selfSignedCertPem);

        TimeSpan readyTimeout = TimeSpan.FromSeconds(60);
        var stopwatch = Stopwatch.StartNew();
        var joinNodeName = joinNodeEndpoint.NodeName;
        var expectedState = desiredState.ToString();
        while (true)
        {
            using var response = await client.GetAsync("/node/state");
            if (response.IsSuccessStatusCode)
            {
                var nodeState = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                var state = nodeState["state"]!.ToString();
                if (state == expectedState)
                {
                    this.logger.LogInformation(
                        $"{joinNodeName}: {joinNodeEndpoint.ClientRpcAddress}/node/state " +
                        $"is reporting {expectedState}.");
                    break;
                }

                this.logger.LogInformation(
                    $"{joinNodeName}: Waiting for " +
                    $"{joinNodeEndpoint.ClientRpcAddress}/node/state " +
                    $"to report {expectedState}. Current state: {state}");
            }
            else
            {
                this.logger.LogInformation(
                    $"{joinNodeName}: Waiting for " +
                    $"{joinNodeEndpoint.ClientRpcAddress}/node/state " +
                    $"to report " +
                    $"{expectedState}. Current statusCode: {response.StatusCode}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"{joinNodeName}: Hit timeout waiting for join node " +
                    $"{joinNodeEndpoint.ClientRpcAddress} to become {expectedState}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        async Task TransitionNodeToTrusted(
            HttpClient serviceClient,
            string nodeId,
            TimeSpan? timeout = null)
        {
            this.logger.LogInformation(
                $"Submitting transition_node_to_trusted proposal for {nodeId}.");
            var proposalContent = new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "transition_node_to_trusted",
                        ["args"] = new JsonObject
                        {
                            ["node_id"] = nodeId,
                            ["valid_from"] = DateTime.UtcNow.ToString("O")
                        }
                    }
                }
            };
            var result = await this.CreateProposal(serviceClient, proposalContent, timeout);
            this.logger.LogInformation(JsonSerializer.Serialize(result, Utils.Options));
        }
    }

    private async Task<JsonObject> WaitForNodeToAppearOnNetwork(
        HttpClient serviceClient,
        string nodeName,
        Func<Task>? onRetry = null)
    {
        TimeSpan readyTimeout = TimeSpan.FromSeconds(60);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            JsonObject? nodes = null;
            using var response = await serviceClient.GetAsync("/node/network/nodes");
            if (response.IsSuccessStatusCode)
            {
                nodes = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                foreach (var node in nodes["nodes"]!.AsArray())
                {
                    if (node?["node_data"] != null)
                    {
                        NodeData nodeData = JsonSerializer.Deserialize<NodeData>(node["node_data"])!;
                        var status = node["status"]!.ToString();
                        if (nodeData.Id == ToId(nodeName) &&
                            (status == "Pending" || status == "Trusted"))
                        {
                            return node.AsObject();
                        }
                    }
                }

                this.logger.LogInformation(
                    $"Waiting for " +
                    $"{serviceClient.BaseAddress}node/network/nodes " +
                    $"to list {nodeName} in its output.");
            }
            else
            {
                this.logger.LogInformation(
                    $"Waiting for " +
                    $"{serviceClient.BaseAddress}node/network/nodes " +
                    $"to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"Hit timeout waiting for join node '{nodeName}' to be reported in " +
                    $"{serviceClient.BaseAddress}node/network/nodes output." +
                    $"Current output: " +
                    $"{(nodes != null ? JsonSerializer.Serialize(nodes, Utils.Options) : "NA")}.");
            }

            if (onRetry != null)
            {
                await onRetry.Invoke();
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<NetworkNode> WaitForNodeStatus(
        HttpClient serviceClient,
        string nodeId,
        string status)
    {
        TimeSpan statusTimeout = TimeSpan.FromSeconds(60);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            List<NetworkNode>? nodes = null;
            using var response = await serviceClient.GetAsync("/node/network/nodes");
            if (response.IsSuccessStatusCode)
            {
                nodes = (await response.Content.ReadFromJsonAsync<NetworkNodeList>())!.Nodes;
                foreach (var node in nodes)
                {
                    if (node.NodeId == nodeId && node.Status == status)
                    {
                        return node;
                    }
                }

                this.logger.LogInformation(
                    $"Waiting for " +
                    $"{serviceClient.BaseAddress}node/network/nodes " +
                    $"to list {nodeId} with status {status} in its output.");
            }
            else
            {
                this.logger.LogInformation(
                    $"Waiting for " +
                    $"{serviceClient.BaseAddress}node/network/nodes " +
                    $"to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }

            if (stopwatch.Elapsed > statusTimeout)
            {
                throw new TimeoutException(
                    $"Hit timeout waiting for node '{nodeId}' to be reported in " +
                    $"{serviceClient.BaseAddress}node/network/nodes output with status {status}." +
                    $"Current output: " +
                    $"{(nodes != null ? JsonSerializer.Serialize(nodes, Utils.Options) : "NA")}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<NetworkNode> WaitForNodeToAppearAsRemovable(
        HttpClient serviceClient,
        string nodeId)
    {
        TimeSpan statusTimeout = TimeSpan.FromSeconds(60);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            using var response = await serviceClient.GetAsync("/node/network/removable_nodes");
            if (response.IsSuccessStatusCode)
            {
                var nodes = (await response.Content.ReadFromJsonAsync<NetworkNodeList>())!.Nodes;
                foreach (var node in nodes)
                {
                    if (node.NodeId == nodeId)
                    {
                        return node;
                    }
                }

                this.logger.LogInformation(
                    $"Waiting for " +
                    $"{serviceClient.BaseAddress}node/network/removable_nodes " +
                    $"to list {nodeId} in its output. " +
                    $"Current output: {JsonSerializer.Serialize(nodes, Utils.Options)}.");
            }
            else
            {
                this.logger.LogInformation(
                    $"Waiting for " +
                    $"{serviceClient.BaseAddress}node/network/removable_nodes " +
                    $"to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }

            if (stopwatch.Elapsed > statusTimeout)
            {
                throw new TimeoutException(
                    $"Hit timeout waiting for node '{nodeId}' to be reported in " +
                    $"{serviceClient.BaseAddress}node/network/removable_nodes output.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<(NetworkNode, long)> WaitForRecoverNodeReady(
    NodeEndpoint recoverNodeEndpoint,
    string serviceCertPem,
    DesiredJoinNodeState desiredJoinNodeState)
    {
        // No retry policy is specified as retries are handled in the loop below.
        var nodeSelfSignedCertPem = await this.GetNodeSelfSignedCert(recoverNodeEndpoint);
        var client = this.GetOrAddNodeClient(
            recoverNodeEndpoint,
            HttpRetries.Policies.NoRetries,
            serviceCertPem,
            nodeSelfSignedCertPem);

        string nodeName = recoverNodeEndpoint.NodeName;
        var nodeEndpoint = recoverNodeEndpoint;
        TimeSpan readyTimeout = TimeSpan.FromSeconds(120);
        var stopwatch = Stopwatch.StartNew();
        var desiredState = desiredJoinNodeState.ToString();
        string? lastState = null;
        while (true)
        {
            using var response = await client.GetAsync("/node/state");
            if (response.IsSuccessStatusCode)
            {
                var nodeState = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                lastState = nodeState["state"]!.ToString();
                if (lastState == desiredState)
                {
                    this.logger.LogInformation(
                        $"{nodeName}: {nodeEndpoint.ClientRpcAddress}/node/state " +
                        $"is reporting {desiredState}.");
                    string nodeId = nodeState["node_id"]!.ToString();
                    var networkNode = (await client.GetFromJsonAsync<NetworkNode>(
                        $"/node/network/nodes/{nodeId}"))!;
                    return
                        (networkNode, Convert.ToInt64(nodeState["last_signed_seqno"]!.ToString()));
                }

                this.logger.LogInformation(
                    $"{nodeName}: Waiting for " +
                    $"{nodeEndpoint.ClientRpcAddress}/node/state " +
                    $"to report " +
                    $"{desiredState}. Current state: {lastState}");
            }
            else
            {
                this.logger.LogInformation(
                    $"{nodeName}: Waiting for " +
                    $"{nodeEndpoint.ClientRpcAddress}/node/state " +
                    $"to report " +
                    $"{desiredState}. Current statusCode: {response.StatusCode}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"{nodeName}: Hit timeout waiting for join node " +
                    $"{nodeEndpoint.ClientRpcAddress} to become '{desiredState}'. " +
                    $"Last state: '{lastState}'.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task WaitForLoadBalancerReady(
        LoadBalancerEndpoint lbEndpoint,
        string serviceCertPem)
    {
        TimeSpan readyTimeout = TimeSpan.FromSeconds(300);
        var stopwatch = Stopwatch.StartNew();
        var lbName = lbEndpoint.Name;
        var endpoint = lbEndpoint.Endpoint;

        // Wait for the CCF service to respond via the load balancer.
        // No retry policy is specified as retries are handled in the loop below.
        var lbClient = this.GetOrAddServiceClient(
            lbEndpoint,
            serviceCertPem,
            HttpRetries.Policies.NoRetries);

        while (true)
        {
            try
            {
                // Use a shorter timeout than the default (100s) so that we retry faster to connect
                // to the LB endpoint that is warming up.
                // Not setting lbClient.Timeout as lbClient is cached/reused so using CTS on a per
                // request basis:
                // https://stackoverflow.com/questions/51478525/httpclient-this-instance-has-already-started-one-or-more-requests-properties-ca
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                using var response = await lbClient.GetAsync("/node/network", cts.Token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    break;
                }

                this.logger.LogInformation(
                    $"Waiting for {endpoint}/node/network to report " +
                    $"200 via the load balancer endpoint. Current status: {response.StatusCode}");
            }
            catch (TaskCanceledException te)
            {
                this.logger.LogInformation(
                    $"{lbName}: Hit HttpClient timeout waiting for {endpoint}/node/network to " +
                    $"report success via the load balancer endpoint. Current " +
                    $"error: {te.Message}.");
            }
            catch (HttpRequestException re)
            {
                this.logger.LogInformation(
                    $"{lbName}: Waiting for {endpoint}/node/network to report " +
                    $"success via the load balancer endpoint. Current " +
                    $"statusCode: {re.StatusCode}, error: {re.Message}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    "Hit timeout waiting for /node/network to respond via the load balancer.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<(NodeEndpoint nodeEndpoint, string nodeId, string serviceCertPem)>
        GetPrimaryNodeEndpoint(
            string networkName,
            JsonObject? providerConfig,
            LoadBalancerEndpoint lbEndpoint)
    {
        var serviceCertPem = await this.GetServiceCert(lbEndpoint.Name, lbEndpoint.Endpoint);
        var serviceClient = this.GetOrAddServiceClient(
            lbEndpoint,
            serviceCertPem,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));
        var nodes = (await serviceClient.GetFromJsonAsync<NetworkNodeList>("/node/network/nodes"))!;
        var primary = nodes.Nodes.Find(n => n.Primary);
        if (primary == null)
        {
            throw new Exception("No node reported as primary by CCF.");
        }

        var nodeEndpoints = await this.nodeProvider.GetNodes(networkName, providerConfig);
        var targetNodeEndpoint = nodeEndpoints.Find(n => ToId(n.NodeName) == primary.NodeData.Id);
        if (targetNodeEndpoint == null)
        {
            throw new Exception(
                $"No nodes nodeData.Id matched primary node nodeData.Id " +
                $"{primary.NodeData.Id}.");
        }

        return (targetNodeEndpoint, primary.NodeId, serviceCertPem);
    }

    private async Task<string> GetServiceCert(
        string nodeName,
        string endpoint,
        Func<Task>? onRetry = null)
    {
        // No retry policy is specified as retries are handled in the loop below.
        using var client = HttpClientManager.NewInsecureClient(
            endpoint,
            this.logger,
            HttpRetries.Policies.NoRetries);

        // Use a shorter timeout than the default (100s) so that we retry faster to connect to the
        // endpoint that is warming up.
        client.Timeout = TimeSpan.FromSeconds(30);

        // At times it takes a while for the endpoint to start responding so giving a large timeout.
        TimeSpan readyTimeout = TimeSpan.FromSeconds(300);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/node/network");
                if (response.IsSuccessStatusCode)
                {
                    var networkState = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                    var value = networkState["service_certificate"]!.ToString();
                    return value;
                }

                this.logger.LogInformation(
                    $"{nodeName}: Waiting for {endpoint}/node/network to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }
            catch (TaskCanceledException te)
            {
                this.logger.LogInformation(
                    $"{nodeName}: Hit HttpClient timeout waiting for {endpoint}/node/network to " +
                    $"report success. Current error: {te.Message}.");
            }
            catch (HttpRequestException re)
            {
                this.logger.LogInformation(
                    $"{nodeName}: Waiting for {endpoint}/node/network to report " +
                    $"success. Current statusCode: {re.StatusCode}, error: {re.Message}.");
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"{nodeName}: Hit timeout waiting for {endpoint}/node/network");
            }

            if (onRetry != null)
            {
                await onRetry.Invoke();
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private async Task<string> GetNodeSelfSignedCert(
        NodeEndpoint nodeEndpoint,
        Func<Task>? onRetry = null)
    {
        var endpoint = nodeEndpoint.ClientRpcAddress;
        var nodeName = nodeEndpoint.NodeName;

        // No retry policy is specified as retries are handled in the loop below.
        using var client = HttpClientManager.NewInsecureClient(
            endpoint,
            this.logger,
            HttpRetries.Policies.NoRetries);

        // Use a shorter timeout than the default (100s) so that we retry faster to connect to the
        // endpoint that is warming up.
        client.Timeout = TimeSpan.FromSeconds(30);

        // At times it takes a while for the endpoint to start responding so giving a large timeout.
        TimeSpan readyTimeout = TimeSpan.FromSeconds(300);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await client.GetAsync("/node/self_signed_certificate");
                if (response.IsSuccessStatusCode)
                {
                    var nodeCert = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                    var value = nodeCert["self_signed_certificate"]!.ToString();
                    return value;
                }

                this.logger.LogInformation(
                    $"{nodeName}: Waiting for {endpoint}/node/self_signed_certificate to report " +
                    $"success. Current statusCode: {response.StatusCode}.");
            }
            catch (TaskCanceledException te)
            {
                this.logger.LogError(
                    $"{nodeName}: Hit HttpClient timeout waiting for " +
                    $"{endpoint}/node/self_signed_certificate to " +
                    $"report success. Current error: {te.Message}.");
            }
            catch (HttpRequestException re)
            {
                this.logger.LogInformation(
                    $"{nodeName}: Waiting for {endpoint}/node/self_signed_certificate to report " +
                    $"success. Current statusCode: {re.StatusCode}, error: {re.Message}.");

                await LogNodeState();
            }

            if (stopwatch.Elapsed > readyTimeout)
            {
                throw new TimeoutException(
                    $"{nodeName}: Hit timeout waiting for {endpoint}/node/self_signed_certificate");
            }

            if (onRetry != null)
            {
                await onRetry.Invoke();
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        async Task LogNodeState()
        {
            try
            {
                // Check the node endorsed endpoint in case that is up and reporting state.
                // This helps debug issues.
                using var debugClient = HttpClientManager.NewInsecureClient(
                    nodeEndpoint.NodeEndorsedRpcAddress,
                    this.logger,
                    HttpRetries.Policies.NoRetries);
                debugClient.Timeout = TimeSpan.FromSeconds(30);

                var nodeStateResponse = await debugClient.GetAsync("/node/state");
                if (nodeStateResponse.IsSuccessStatusCode)
                {
                    var nodeState =
                        (await nodeStateResponse.Content.ReadFromJsonAsync<JsonObject>())!;
                    this.logger.LogInformation(
                        $"{nodeName}: /node/state endpoint on node endorsed endpoint is " +
                        $"reporting: " +
                        $"{JsonSerializer.Serialize(nodeState)}.");
                }
                else
                {
                    this.logger.LogError(
                        $"{nodeName}: Could not query /node/state on node endorsed endpoint to " +
                        $"glean further information. Current statusCode: " +
                        $"{nodeStateResponse.StatusCode}.");
                }
            }
            catch (Exception e)
            {
                // Ignore any failures of this method as this was invoked to gather better
                // debugging info.
                this.logger.LogError(
                    $"{nodeName}: Could not query /node/state on node endorsed endpoint to " +
                    $"glean further information. Error: {e}.");
            }
        }
    }

    private HttpClient GetOrAddNodeClient(
        NodeEndpoint nodeEndpoint,
        IAsyncPolicy<HttpResponseMessage> retryPolicy,
        string serviceCertPem,
        string nodeSelfSignedCertPem)
    {
        string nodeName = nodeEndpoint.NodeName;
        string clientRpcAddress = nodeEndpoint.ClientRpcAddress;

        return this.httpClientManager.GetOrAddClient(
            clientRpcAddress,
            new List<string>()
            {
                serviceCertPem,
                nodeSelfSignedCertPem
            },
            retryPolicy,
            nodeName);
    }

    private HttpClient GetOrAddServiceClient(
        NodeEndpoint nodeEndpoint,
        string serviceCertPem,
        IAsyncPolicy<HttpResponseMessage> retryPolicy)
    {
        return this.httpClientManager.GetOrAddClient(
            nodeEndpoint.ClientRpcAddress,
            retryPolicy,
            serviceCertPem,
            nodeEndpoint.NodeName);
    }

    private HttpClient GetOrAddServiceClient(
        LoadBalancerEndpoint lbEndpoint,
        string serviceCertPem,
        IAsyncPolicy<HttpResponseMessage> retryPolicy)
    {
        return this.httpClientManager.GetOrAddClient(
            lbEndpoint.Endpoint,
            retryPolicy,
            serviceCertPem,
            lbEndpoint.Name);
    }

    private async Task<JsonObject> CreateProposal(
        HttpClient ccfClient,
        JsonObject content,
        TimeSpan? timeout = null)
    {
        var signingConfig = await this.ccfClientManager.GetSigningConfig();
        var payload = await Cose.CreateGovCoseSign1Message(
            signingConfig.CoseSignKey,
            GovMessageType.Proposal,
            content.ToJsonString());
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals:create" +
            $"?api-version={this.ccfClientManager.GetGovApiVersion()}",
            payload);
        using HttpResponseMessage response = await ccfClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
        await response.WaitGovTransactionCommittedAsync(this.logger, ccfClient, timeout);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    private async Task<JsonObject> VoteAccept(HttpClient ccfClient, string proposalId)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "export function vote (proposal, proposerId) { return true }"
        };

        var signingConfig = await this.ccfClientManager.GetSigningConfig();
        var payload = await Cose.CreateGovCoseSign1Message(
            signingConfig.CoseSignKey,
            GovMessageType.Ballot,
            ballot.ToJsonString(),
            proposalId.ToString());

        using var cert = X509Certificate2.CreateFromPem(signingConfig.CoseSignKey.Certificate);
        var memberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower();
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals/{proposalId}/ballots/" +
            $"{memberId}:submit" +
            $"?api-version={this.ccfClientManager.GetGovApiVersion()}",
            payload);
        using HttpResponseMessage response = await ccfClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
        await response.WaitGovTransactionCommittedAsync(this.logger, ccfClient);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    private async Task<JsonObject> SubmitRecoveryShare(
        HttpClient ccfClient,
        string memberId,
        CoseSignKey coseSignKey,
        JsonObject content)
    {
        var payload = await Cose.CreateGovCoseSign1Message(
            coseSignKey,
            GovMessageType.RecoveryShare,
            content.ToJsonString());
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/recovery/members/{memberId}:recover" +
            $"?api-version={this.ccfClientManager.GetGovApiVersion()}",
            payload);
        using HttpResponseMessage response = await ccfClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
        await response.WaitGovTransactionCommittedAsync(this.logger, ccfClient);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    private async Task<List<NetworkNode>> PickNodesToRemove(
        string networkName,
        JsonObject? providerConfig,
        HttpClient primaryClient,
        string primaryNodeId,
        int numNodesToRemove)
    {
        var nodes =
            (await primaryClient.GetFromJsonAsync<NetworkNodeList>("/node/network/nodes"))!.Nodes;
        this.logger.LogInformation(
            $"Current nodes: {JsonSerializer.Serialize(nodes, Utils.Options)}.");
        var consensus = (await primaryClient.GetFromJsonAsync<Consensus>("/node/consensus"))!;
        this.logger.LogInformation(
            $"Current consensus: {JsonSerializer.Serialize(consensus, Utils.Options)}.");

        if (consensus.Details.PrimaryId != primaryNodeId)
        {
            // Primary has shifted. Abort for now.
            throw new Exception(
                $"Primary endpoint changed from '{primaryNodeId}' to " +
                $"'{consensus.Details.PrimaryId}' while determining nodes to remove. " +
                $"Try again later.");
        }

        List<NetworkNode> nodesToRemove = new();

        var nodesHealth = await this.nodeProvider.GetNodesHealth(networkName, providerConfig);
        this.logger.LogInformation($"Nodes health: " +
            $"{JsonSerializer.Serialize(nodesHealth, Utils.Options)}");

        // Order of preference:
        // - pick nodes that anyway need replacement
        // - then pick nodes that are in Pending state but not part of the consensus API output
        //   as this most likely indicates a failed/aborted join attempt.
        // - then picking nodes by their ack time.
        List<string> candidateNodes =
            nodes
            .Where(n => nodesHealth.Any(nh => ToId(nh.Name) == ToId(n.NodeData.Name)
                && nh.Status == nameof(NodeStatus.NeedsReplacement)))
            .Select(n => n.NodeId)
            .ToList();

        // TODO (gsinha): Uncomment once remove_node behavior for a node in "Pending" state that
        // was never trusted is understood.
        ////List<string> notPartOfConsensusNodes =
        //// nodes.Where(n => n.Status == "Pending" && !consensus.Details.Acks.ContainsKey(n.NodeId))
        //// .Select(n => n.NodeId)
        //// .ToList();
        ////candidateNodes.AddRange(notPartOfConsensusNodes.Except(candidateNodes));

        List<string> slowestNodes = consensus.Details.Acks
            .Where(x => !candidateNodes.Contains(x.Key))
            .OrderByDescending(ack => ack.Value.LastReceivedMs)
            .Select(n => n.Key)
            .ToList();
        candidateNodes.AddRange(slowestNodes);

        this.logger.LogInformation(
            $"Order of nodes to pick for removal: " +
            $"{JsonSerializer.Serialize(candidateNodes, Utils.Options)}.");
        int requestedNodesToRemove = numNodesToRemove;
        foreach (var nodeId in candidateNodes)
        {
            var node = nodes.Find(n => n.NodeId == nodeId && n.Status != "Retired");
            if (node == null)
            {
                continue;
            }

            if (node.Primary)
            {
                // Primary has shifted and we picked a node that reported itself as primary but
                // the Consensus ack output should not have carried its Id if the consensus output
                // was queried from this node itself. Abort for now.
                throw new Exception(
                    $"'{node.NodeId}' is reporting itself as Primary but was not considered so " +
                    $"during Consensus output query as that was taken from primary endpoint " +
                    $"'{primaryNodeId}'. So primary node has shifted while determining nodes " +
                    $"to remove. Try again later.");
            }

            nodesToRemove.Add(node);
            numNodesToRemove--;
            if (numNodesToRemove == 0)
            {
                break;
            }
        }

        this.logger.LogInformation(
            $"Picked {nodesToRemove.Count} node(s) to remove. " +
            $"{requestedNodesToRemove} number of nodes were explicitly requested: " +
            $"{JsonSerializer.Serialize(nodesToRemove, Utils.Options)}");

        return nodesToRemove;
    }

    private async Task<bool> CleanupRetiredNodes(
        string networkName,
        JsonObject? providerConfig,
        HttpClient primaryClient)
    {
        var nodes =
            (await primaryClient.GetFromJsonAsync<NetworkNodeList>("/node/network/nodes"))!
            .Nodes;
        var retiredNodes = nodes
            .Where(n => n.Status == "Retired")
            .ToList();
        if (retiredNodes.Any())
        {
            this.logger.LogInformation(
                $"Current nodes: {JsonSerializer.Serialize(nodes, Utils.Options)}.");
        }

        List<Task> retireTasks = new();
        foreach (var node in retiredNodes)
        {
            var n = node; // To avoid closure issues.
            retireTasks.Add(Task.Run(async () =>
            {
                // Wait for node to appear in removal_nodes api.
                // Delete node from infra.
                // Invoke DELETE nodes API.
                try
                {
                    await this.WaitForNodeToAppearAsRemovable(primaryClient, n.NodeId);
                    this.logger.LogInformation(
                        $"Already retired node {n.NodeId} is being reported as a removable node. " +
                        $"Deleting the node from the infra.");
                    await this.nodeProvider.DeleteNode(
                        networkName,
                        n.NodeData.Name,
                        n.NodeData,
                        providerConfig);
                    this.logger.LogInformation($"Infra node removed. Issuing DELETE {n.NodeId}.");
                    await primaryClient.DeleteAsync($"/node/network/nodes/{n.NodeId}");
                }
                catch (TimeoutException te)
                {
                    // https://github.com/microsoft/CCF/issues/6604
                    // TODO (gsinha): Need to ignore this till we fix the issue of pre-DR nodes
                    // that are marked retired do not appear in removable nodes api output so the
                    // logic times out.
                    this.logger.LogError(
                        te,
                        "Ignoring timeout exception in WaitForNodeToAppearAsRemovable");
                }
            }));
        }

        await Task.WhenAll(retireTasks);
        return retireTasks.Any();
    }

    private async Task<bool> CleanupRetiredNodesPostRecovery(
        string networkName,
        JsonObject? providerConfig,
        HttpClient serviceClient)
    {
        // TODO (gsinha): Below logic to issue a DELETE has no affect due to
        // https://github.com/microsoft/CCF/issues/6604.
        var nodes =
            (await serviceClient.GetFromJsonAsync<NetworkNodeList>("/node/network/nodes"))!
            .Nodes;
        var retiredNodes = nodes
            .Where(n => n.Status == "Retired")
            .ToList();
        if (retiredNodes.Any())
        {
            this.logger.LogInformation(
                $"Current nodes: {JsonSerializer.Serialize(nodes, Utils.Options)}.");
        }

        List<Task> retireTasks = new();
        foreach (var node in retiredNodes)
        {
            var n = node; // To avoid closure issues.
            retireTasks.Add(Task.Run(async () =>
            {
                this.logger.LogInformation($"Issuing DELETE {n.NodeId}.");
                await serviceClient.DeleteAsync($"/node/network/nodes/{n.NodeId}");
            }));
        }

        await Task.WhenAll(retireTasks);
        return retireTasks.Any();
    }

    private async Task<bool> RemoveNodes(
        string networkName,
        JsonObject? providerConfig,
        List<NetworkNode> nodesToRemove,
        HttpClient primaryClient)
    {
        List<Task> removeTasks = new();
        foreach (var node in nodesToRemove)
        {
            var n = node; // To avoid closure issues.
            removeTasks.Add(Task.Run(async () =>
            {
                // Submit remove_node proposal if status != Retired.
                // Wait for node to be reported as retired.
                // Wait for node to appear in removal_nodes api.
                // Delete node from infra.
                // Invoke DELETE nodes API.
                if (n.Status != "Retired")
                {
                    await ProposeRemoveNode(primaryClient, n.NodeId);
                    await this.WaitForNodeStatus(primaryClient, n.NodeId, "Retired");
                    this.logger.LogInformation($"Node {n.NodeId} is reported as Retired.");
                }

                await this.WaitForNodeToAppearAsRemovable(primaryClient, n.NodeId);
                this.logger.LogInformation(
                    $"Node {n.NodeId} is being reported as a removable node. " +
                    $"Deleting the node from the infra.");
                await this.nodeProvider.DeleteNode(
                    networkName,
                    n.NodeData.Name,
                    n.NodeData,
                    providerConfig);

                this.logger.LogInformation($"Infra node removed. Issuing DELETE {n.NodeId}.");
                await primaryClient.DeleteAsync($"/node/network/nodes/{n.NodeId}");
            }));
        }

        await Task.WhenAll(removeTasks);
        return removeTasks.Any();

        async Task ProposeRemoveNode(HttpClient serviceClient, string nodeId)
        {
            this.logger.LogInformation(
                $"Submitting remove_node proposal for {nodeId}.");
            var proposalContent = new JsonObject
            {
                ["actions"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "remove_node",
                            ["args"] = new JsonObject
                            {
                                ["node_id"] = nodeId
                            }
                        }
                    }
            };
            var result = await this.CreateProposal(serviceClient, proposalContent);
            this.logger.LogInformation(JsonSerializer.Serialize(result, Utils.Options));
        }
    }

    private async Task<bool> AddNodes(
        string networkName,
        JsonObject? providerConfig,
        int numNodesToCreate,
        NodeEndpoint primaryNodeEndpoint,
        List<string> san,
        string serviceCertPem,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        int startOrdinal)
    {
        List<Task> joinTasks = new();
        while (numNodesToCreate != 0)
        {
            int nodeOrdinal = startOrdinal; // Make a local copy to avoid closure issues.
            joinTasks.Add(Task.Run(async () =>
            {
                var joinNodeName = networkName + "-" + nodeOrdinal;
                var nodeData = new NodeData
                {
                    Name = joinNodeName,
                    Id = ToId(joinNodeName)
                };
                var newNodeEndpoint = await this.nodeProvider.CreateJoinNode(
                    joinNodeName,
                    networkName,
                    serviceCertPem,
                    primaryNodeEndpoint.NodeName,
                    primaryNodeEndpoint.ClientRpcAddress,
                    nodeLogLevel,
                    policyOption,
                    nodeData,
                    san,
                    providerConfig);

                // Check before waiting for the node to become ready.
                await this.CheckNodeHealthy(networkName, joinNodeName, providerConfig);

                await this.WaitForJoinNodeReady(
                    networkName,
                    providerConfig,
                    primaryNodeEndpoint,
                    newNodeEndpoint,
                    serviceCertPem,
                    DesiredJoinNodeState.PartOfNetwork);

                this.logger.LogInformation($"Node {newNodeEndpoint.NodeName} is up at " +
                    $"{newNodeEndpoint.ClientRpcAddress}.");
            }));

            numNodesToCreate--;
            startOrdinal++;
        }

        await Task.WhenAll(joinTasks);
        return joinTasks.Any();
    }

    private async Task CheckNodeHealthy(
        string networkName,
        string nodeName,
        JsonObject? providerConfig)
    {
        var nodeHealth = await this.nodeProvider.GetNodeHealth(
            networkName,
            nodeName,
            providerConfig);
        if (nodeHealth.Status == nameof(NodeStatus.NeedsReplacement))
        {
            throw new Exception(
                $"Node instance {nodeName} is reporting unhealthy: " +
                $"{JsonSerializer.Serialize(nodeHealth, Utils.Options)}");
        }
    }

    private async Task AddAndAcceptRecoveryOperator(
        HttpClient ccfClient,
        string networkName,
        string memberName,
        string signingCert,
        string encryptionPublicKey,
        JsonObject recoveryServiceData,
        JsonObject? providerConfig)
    {
        this.logger.LogInformation($"Adding recovery member {memberName}.");
        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "set_member",
                    ["args"] = new JsonObject
                    {
                        ["cert"] = signingCert,
                        ["encryption_pub_key"] = encryptionPublicKey,
                        ["recovery_role"] = "Owner",
                        ["member_data"] = new JsonObject
                        {
                            ["identifier"] = memberName,
                            ["isRecoveryOperator"] = true,
                            ["recoveryService"] = recoveryServiceData
                        }
                    }
                }
            }
        };

        this.logger.LogInformation($"Recovery member proposal content: " +
            $"{JsonSerializer.Serialize(proposalContent)}.");

        var proposal = await this.CreateProposal(ccfClient, proposalContent);
        string proposalId = proposal["proposalId"]!.ToString();
        if (proposal["proposalState"]!.ToString() == "Open")
        {
            this.logger.LogInformation($"Accepting set_member proposal {proposalId}.");
            proposal = await this.VoteAccept(ccfClient, proposalId);
        }

        if (proposal["proposalState"]!.ToString() != "Accepted")
        {
            throw new ApiException(
                HttpStatusCode.MethodNotAllowed,
                "ProposalNotAccepted",
                $"Proposal to add recovery operator member " +
                $"'{proposalId}' is not in an accepted state. " +
                $"Proposal state: {JsonSerializer.Serialize(proposal)}");
        }
    }
}
