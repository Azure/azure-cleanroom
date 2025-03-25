// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfProvider;
using Docker.DotNet;
using Docker.DotNet.Models;
using LoadBalancerProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCcfProvider;

public class DockerNginxLoadBalancerProvider : ICcfLoadBalancerProvider
{
    private const string ProviderFolderName = "virtual";
    private const int NginxPort = 443;

    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly DockerClient client;

    public DockerNginxLoadBalancerProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
    }

    public async Task<LoadBalancerEndpoint> CreateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
        JsonObject? providerConfig)
    {
        return await this.CreateLoadBalancerContainer(
            lbName,
            networkName,
            servers,
            hostPort: null, // Dynamic assignment.
            providerConfig);
    }

    public async Task<LoadBalancerEndpoint> UpdateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
        JsonObject? providerConfig)
    {
        // For the docker environment we attempt to delete and recreate the container instance
        // on the assumption that it was already running and the new container is configured to
        // use the same hostPort so that clients don't see a change in the port value that the
        // LB endpoint was initially listening on.
        string containerName = lbName;
        var container = await this.client.GetContainerByName(containerName);
        int publicPort = container.GetPublicPort(NginxPort);
        await this.DeleteLoadBalancer(networkName, providerConfig);
        return await this.CreateLoadBalancerContainer(
            lbName,
            networkName,
            servers,
            publicPort.ToString(),
            providerConfig);
    }

    public async Task DeleteLoadBalancer(string networkName, JsonObject? providerConfig)
    {
        await this.client.DeleteContainers(
            this.logger,
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

    public async Task<LoadBalancerEndpoint> CreateLoadBalancerContainer(
        string lbName,
        string networkName,
        List<string> servers,
        string? hostPort,
        JsonObject? providerConfig)
    {
        string containerName = lbName;

        string workspaceDir =
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Directory.GetCurrentDirectory();
        var scratchDir = workspaceDir + $"/{ProviderFolderName}/{networkName}/nginx";

        Directory.CreateDirectory(scratchDir);

        List<string> nginxConfigTemplate =
            (await File.ReadAllLinesAsync("templates/nginx.conf"))!.ToList();
        int serverEntriesIndex =
            nginxConfigTemplate.FindIndex(0, (line) => line.Contains("$serverEntries"));

        List<string> nginxConfig = new();
        nginxConfig.AddRange(nginxConfigTemplate.Take(serverEntriesIndex));
        foreach (var server in servers)
        {
            nginxConfig.Add(nginxConfigTemplate[serverEntriesIndex].
                Replace("$serverEntries", $"server {server};"));
        }

        nginxConfig.AddRange(nginxConfigTemplate.Skip(serverEntriesIndex + 1));

        var nginxConfigPath = scratchDir + "/nginx.conf";

        await File.WriteAllLinesAsync(nginxConfigPath, nginxConfig);

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(scratchDir);

        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.CcfNginxImage(),
            Tag = ImageUtils.CcfNginxTag(),
        };

        await this.client.Images.CreateImageAsync(
            imageParams,
            authConfig: null,
            new Progress<JSONMessage>(m => this.logger.LogInformation(m.ToProgressMessage())));

        var createParams = new CreateContainerParameters
        {
            Labels = new Dictionary<string, string>
                {
                    {
                        DockerConstants.CcfNetworkNameTag,
                        networkName
                    },
                    {
                        DockerConstants.CcfNetworkTypeTag,
                        "load-balancer"
                    },
                    {
                        DockerConstants.CcfNetworkResourceNameTag,
                        lbName
                    }
                },
            Name = containerName,
            Image = $"{imageParams.FromImage}:{imageParams.Tag}",
            Env = new List<string>
                {
                $"CONFIG_DATA_TGZ={tgzConfigData}"
                },
            ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                {
                    $"{NginxPort}/tcp", new EmptyStruct()
                },
                {
                    "80/tcp", new EmptyStruct()
                }
                },
            HostConfig = new HostConfig
            {
                NetworkMode = networkName,
                PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                    {
                        $"{NginxPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                HostPort = hostPort
                            }
                        }
                    }
                    }
            }
        };
        var container = await this.client.CreateOrGetContainer(createParams);

        await this.client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        // Fetch again after starting to get the port mapping information.
        container = await this.client.GetContainerById(container.ID);
        return this.ToLbEndpoint(container);
    }

    public async Task<ContainerListResponse?> TryGetLoadBalancerContainer(
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
                    { $"{DockerConstants.CcfNetworkTypeTag}=load-balancer", true }
                }
            }
        });

        return containers.FirstOrDefault();
    }

    private LoadBalancerEndpoint ToLbEndpoint(ContainerListResponse container)
    {
        int publicPort = container.GetPublicPort(NginxPort);
        var host = this.GetLbHostName();

        return new LoadBalancerEndpoint
        {
            Name = container.Labels[DockerConstants.CcfNetworkResourceNameTag],
            Endpoint = $"https://{host}:{publicPort}"
        };
    }

    private string GetLbHostName()
    {
        var host = IsGitHubActionsEnv() ? "172.17.0.1" : "host.docker.internal";
        return host;

        static bool IsGitHubActionsEnv()
        {
            return Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        }
    }
}
