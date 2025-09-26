// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AciLoadBalancer;
using CcfProvider;
using Docker.DotNet.Models;
using LoadBalancerProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCcfProvider;

public class DockerEnvoyLoadBalancerProvider : DockerLoadBalancerProvider, ICcfLoadBalancerProvider
{
    public DockerEnvoyLoadBalancerProvider(
        ILogger logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
    }

    public override async Task<LoadBalancerEndpoint> CreateLoadBalancerContainer(
        string lbName,
        string networkName,
        List<string> servers,
        List<string> agentServers,
        string? mainHostPort,
        string? agentHostPort,
        JsonObject? providerConfig)
    {
        string containerName = lbName;

        string workspaceDir =
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Directory.GetCurrentDirectory();
        var scratchDir = workspaceDir + $"/{ProviderFolderName}/{networkName}/envoy";

        Directory.CreateDirectory(scratchDir);

        var services = new List<TcpService>
            {
                new()
                {
                    ListenerPort = "443",
                    Upstreams = servers.Select(ToAddressPortTuple)
                },
                new()
                {
                    ListenerPort = "444",
                    Upstreams = agentServers.Select(ToAddressPortTuple)
                }
            };

        string yaml = EnvoyConfigGenerator.GenerateEnvoyYaml(services);

        static (string, string) ToAddressPortTuple(string s)
        {
            var parts = s.Split(":");
            if (parts.Length != 2)
            {
                throw new Exception($"Expecting only two parts but got {parts.Length}. Input: {s}");
            }

            return new(parts[0], parts[1]);
        }

        var envoyConfigPath = scratchDir + "/l4-proxy-config.yaml";
        await File.WriteAllTextAsync(envoyConfigPath, yaml);

        // Pack the contents of the directory into base64 encoded tar gzip string which then
        // gets uncompressed and expanded in the container.
        string tgzConfigData = await Utils.PackDirectory(scratchDir);

        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.CcrProxyImage(),
            Tag = ImageUtils.CcrProxyTag(),
        };

        await this.Client.Images.CreateImageAsync(
            imageParams,
            authConfig: null,
            new Progress<JSONMessage>(m => this.Logger.LogInformation(m.ToProgressMessage())));

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
                    $"{MainPort}/tcp", new EmptyStruct()
                },
                {
                    $"{AgentPort}/tcp", new EmptyStruct()
                }
            },
            Entrypoint = new List<string>
            {
                "/bin/bash",
                "l4-lb/bootstrap.sh"
            },
            HostConfig = new HostConfig
            {
                NetworkMode = networkName,
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{MainPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                HostPort = mainHostPort
                            }
                        }
                    },
                    {
                        $"{AgentPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                HostPort = agentHostPort
                            }
                        }
                    }
                }
            }
        };
        var container = await this.Client.CreateOrGetContainer(createParams);

        await this.Client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        // Fetch again after starting to get the port mapping information.
        container = await this.Client.GetContainerById(container.ID);

        var assignedMainHostPort = container.GetPublicPort(MainPort);
        var assignedAgentHostPort = container.GetPublicPort(AgentPort);
        this.HostPortMapppings[lbName] = (assignedMainHostPort, assignedAgentHostPort);

        return this.ToLbEndpoint(container);
    }
}
