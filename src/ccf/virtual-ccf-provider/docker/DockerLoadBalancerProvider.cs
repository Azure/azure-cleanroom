// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Docker.DotNet;
using Docker.DotNet.Models;
using LoadBalancerProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCcfProvider;

public abstract class DockerLoadBalancerProvider
{
    protected const string ProviderFolderName = "virtual";

    protected const int AgentPort = 444;
    protected const int MainPort = 443;

    public DockerLoadBalancerProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.Logger = logger;
        this.Configuration = configuration;
        this.Client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
        this.HostPortMapppings = new();
    }

    protected ConcurrentDictionary<string, (int? mainPort, int? agentPort)> HostPortMapppings
    {
        get;
    }

    protected DockerClient Client { get; }

    protected IConfiguration Configuration { get; }

    protected ILogger Logger { get; }

    public async Task<LoadBalancerEndpoint> CreateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
        List<string> agentServers,
        JsonObject? providerConfig)
    {
        // For docker environment we attempt to reuse any previously assigned host port values
        // for a given network name so that clients don't see a change in the port value when
        // the LB endpoint gets deleted and recreated for the same network (eg when testing
        // network recovery).
        this.HostPortMapppings.TryGetValue(lbName, out var portMappings);
        return await this.CreateLoadBalancerContainer(
            lbName,
            networkName,
            servers,
            agentServers,
            portMappings.mainPort?.ToString(),
            portMappings.agentPort?.ToString(),
            providerConfig);
    }

    public abstract Task<LoadBalancerEndpoint> CreateLoadBalancerContainer(
        string lbName,
        string networkName,
        List<string> servers,
        List<string> agentServers,
        string? mainHostPort,
        string? agentHostPort,
        JsonObject? providerConfig);

    public async Task DeleteLoadBalancer(string networkName, JsonObject? providerConfig)
    {
        await this.Client.DeleteContainers(
            this.Logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                    { $"{DockerConstants.CcfNetworkTypeTag}=load-balancer", true }
                }
            }
        });
    }

    public string GenerateLoadBalancerFqdn(
        string lbName,
        string networkName,
        JsonObject? providerConfig)
    {
        return this.GetLbHostName();
    }

    public async Task<LoadBalancerEndpoint> GetLoadBalancerEndpoint(
        string networkName,
        JsonObject? providerConfig)
    {
        return await this.TryGetLoadBalancerEndpoint(networkName, providerConfig) ??
            throw new Exception($"No load balancer endpoint found for {networkName}.");
    }

    public async Task<LoadBalancerHealth> GetLoadBalancerHealth(
    string networkName,
    JsonObject? providerConfig)
    {
        var container = await this.TryGetLoadBalancerContainer(networkName, providerConfig);
        if (container == null)
        {
            return new LoadBalancerHealth
            {
                Status = nameof(LbStatus.NeedsReplacement),
                Reasons = new List<LoadBalancerProvider.Reason>
                {
                    new()
                    {
                        Code = "NotFound",
                        Message = $"No load balancer endpoint for network {networkName} was found."
                    }
                }
            };
        }

        return ToLbHealth(container);

        LoadBalancerHealth ToLbHealth(ContainerListResponse container)
        {
            var status = LbStatus.Ok;
            var reasons = new List<LoadBalancerProvider.Reason>();
            if (container.State == "exited")
            {
                status = LbStatus.NeedsReplacement;
                var code = "ContainerExited";
                var message = $"Container {container.ID} has exited: {container.Status}.";
                reasons.Add(new() { Code = code, Message = message });
            }

            var ep = this.ToLbEndpoint(container);
            return new LoadBalancerHealth
            {
                Name = ep.Name,
                Endpoint = ep.Endpoint,
                Status = status.ToString(),
                Reasons = reasons
            };
        }
    }

    public async Task<ContainerListResponse?> TryGetLoadBalancerContainer(
        string networkName,
        JsonObject? providerConfig)
    {
        var containers = await this.Client.GetContainers(
            this.Logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfNetworkNameTag}={networkName}", true },
                    { $"{DockerConstants.CcfNetworkTypeTag}=load-balancer", true }
                }
            }
        });

        return containers.FirstOrDefault();
    }

    public async Task<LoadBalancerEndpoint?> TryGetLoadBalancerEndpoint(
        string networkName,
        JsonObject? providerConfig)
    {
        var container = await this.TryGetLoadBalancerContainer(networkName, providerConfig);
        if (container != null)
        {
            return this.ToLbEndpoint(container);
        }

        return null;
    }

    public async Task<LoadBalancerEndpoint> UpdateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
        List<string> agentServers,
        JsonObject? providerConfig)
    {
        // For the docker environment we attempt to delete and recreate the container instance
        // on the assumption that it was already running and the new container is configured to
        // use the same hostPort so that clients don't see a change in the port value that the
        // LB endpoint was initially listening on.
        string containerName = lbName;
        var container = await this.Client.GetContainerByName(containerName);
        int mainHostPort = container.GetPublicPort(MainPort);
        int agentHostPort = container.GetPublicPort(AgentPort);
        await this.DeleteLoadBalancer(networkName, providerConfig);
        return await this.CreateLoadBalancerContainer(
            lbName,
            networkName,
            servers,
            agentServers,
            mainHostPort.ToString(),
            agentHostPort.ToString(),
            providerConfig);
    }

    protected LoadBalancerEndpoint ToLbEndpoint(ContainerListResponse container)
    {
        int publicPort = container.GetPublicPort(MainPort);
        int publicAgentPort = container.GetPublicPort(AgentPort);
        var host = this.GetLbHostName();

        return new LoadBalancerEndpoint
        {
            Name = container.Labels[DockerConstants.CcfNetworkResourceNameTag],
            Endpoint = $"https://{host}:{publicPort}",
            AgentEndpoint = $"https://{host}:{publicAgentPort}"
        };
    }

    private string GetLbHostName()
    {
        var host = IsGitHubActionsEnv() || IsCodespacesEnv() ? "172.17.0.1" : "host.docker.internal";
        return host;

        static bool IsGitHubActionsEnv()
        {
            return Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        }

        static bool IsCodespacesEnv()
        {
            return Environment.GetEnvironmentVariable("CODESPACES") == "true";
        }
    }
}