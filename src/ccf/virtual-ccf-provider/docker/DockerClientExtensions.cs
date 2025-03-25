// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text.Json;
using CcfProvider;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using static VirtualCcfProvider.DockerRecoveryServiceInstanceProvider;

namespace VirtualCcfProvider;

internal static class DockerClientEx
{
    public static int GetPublicPort(
        this ContainerListResponse container,
        int privatePort)
    {
        var clientPort = container.Ports.FirstOrDefault(
            p => p.Type == "tcp" && p.PrivatePort == privatePort);
        if (clientPort == null)
        {
            if (container.Status != "running")
            {
                // If the container is not running then a public port mapping will not be found.
                return 0;
            }

            throw new Exception(
                $"Expecting port mapping for {privatePort}/tcp but found following ports: " +
                $"{JsonSerializer.Serialize(container.Ports, Utils.Options)}.");
        }

        int publicPort = clientPort.PublicPort;
        return publicPort;
    }

    public static async Task<ContainerListResponse> GetContainerById(
        this DockerClient client,
        string containerId)
    {
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    {
                        "id", new Dictionary<string, bool>
                        {
                            { $"{containerId}", true }
                        }
                    }
                }
            });

        if (containers.Count != 1)
        {
            throw new Exception(
                $"Expecting 1 container with ID {containerId} but found {containers.Count}.");
        }

        return containers[0];
    }

    public static async Task<ContainerListResponse> GetContainerByName(
        this DockerClient client,
        string containerName)
    {
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    {
                        "name", new Dictionary<string, bool>
                        {
                            { $"^{containerName}$", true }
                        }
                    }
                }
            });

        if (containers.Count != 1)
        {
            throw new Exception(
                $"Expecting 1 container with name {containerName} but found {containers.Count}" +
                $". Details: {JsonSerializer.Serialize(containers, Utils.Options)}");
        }

        return containers[0];
    }

    public static async Task<bool> ContainerExists(this DockerClient client, string containerName)
    {
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    {
                        "name", new Dictionary<string, bool>
                        {
                            { $"^{containerName}$", true }
                        }
                    }
                }
            });

        return containers.Count > 0;
    }

    public static async Task<bool> ContainerExistsById(this DockerClient client, string containerId)
    {
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    {
                        "id", new Dictionary<string, bool>
                        {
                            { $"{containerId}", true }
                        }
                    }
                }
            });

        return containers.Count > 0;
    }

    public static async Task<List<ContainerListResponse>> GetContainers(
        this DockerClient client,
        ILogger logger,
        Dictionary<string, IDictionary<string, bool>> filters)
    {
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = filters
            });

        return containers.ToList();
    }

    public static async Task DeleteContainers(
        this DockerClient client,
        ILogger logger,
        Dictionary<string, IDictionary<string, bool>> filters)
    {
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = filters
            });

        logger.LogInformation($"Found {containers.Count} docker container(s) to delete with " +
            $"filter: {JsonSerializer.Serialize(filters, Utils.Options)}.");
        foreach (var container in containers)
        {
            logger.LogInformation($"Deleting container {container.ID}");
            await client.Containers.RemoveContainerAsync(
                container.ID,
                new ContainerRemoveParameters
                {
                    Force = true
                });
            var timeout = TimeSpan.FromSeconds(30);
            var stopwatch = Stopwatch.StartNew();
            while (await ContainerExistsById(client, container.ID))
            {
                logger.LogInformation($"Waiting for container {container.ID} to get removed.");
                if (stopwatch.Elapsed > timeout)
                {
                    throw new Exception($"Hit timeout waiting for {container.ID} to get removed.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
    }

    public static async Task<ContainerListResponse> CreateOrGetContainer(
        this DockerClient client,
        CreateContainerParameters createParams)
    {
        try
        {
            var container = await client.Containers.CreateContainerAsync(createParams);
            return await client.GetContainerById(container.ID);
        }
        catch (DockerApiException de) when (de.StatusCode == HttpStatusCode.Conflict)
        {
            return await client.GetContainerByName(createParams.Name);
        }
    }

    public static NodeEndpoint ToNodeEndpoint(this ContainerListResponse container)
    {
        int publicPort = container.GetPublicPort(Ports.RpcMainPort);
        int publicDebugPort = container.GetPublicPort(Ports.RpcDebugPort);
        var host = IsGitHubActionsEnv() ? "172.17.0.1" : "host.docker.internal";

        return new NodeEndpoint
        {
            NodeName = container.Labels[DockerConstants.CcfNetworkResourceNameTag],
            ClientRpcAddress = $"{host}:{publicPort}",
            NodeEndorsedRpcAddress = $"{host}:{publicDebugPort}"
        };
    }

    public static RecoveryAgentEndpoint ToRecoveryAgentEndpoint(this EnvoyEndpoint ep)
    {
        // As envoy will front the calls return its endpoint as the agent endpoint.
        return new RecoveryAgentEndpoint
        {
            Name = ep.Name,
            Endpoint = ep.Endpoint
        };
    }

    public static EnvoyEndpoint ToEnvoyEndpoint(
        this ContainerListResponse container,
        string resourceNameTag)
    {
        int publicPort = container.GetPublicPort(Ports.EnvoyPort);
        var host = IsGitHubActionsEnv() ? "172.17.0.1" : "host.docker.internal";

        return new EnvoyEndpoint
        {
            Name = container.Labels[resourceNameTag],
            Endpoint = $"https://{host}:{publicPort}"
        };
    }

    public static async Task<EnvoyEndpoint> CreateEnvoyProxyContainer(
        this DockerClient client,
        ILogger logger,
        string envoyDestinationEndpoint,
        int envoyDestinationPort,
        string containerName,
        string serviceName,
        string hostServiceCertDir,
        string resourceNameTag,
        Dictionary<string, string> labels)
    {
        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.CcrProxyImage(),
            Tag = ImageUtils.CcrProxyTag(),
        };
        await client.Images.CreateImageAsync(
            imageParams,
            authConfig: null,
            new Progress<JSONMessage>(m => logger.LogInformation(m.ToProgressMessage())));

        var createParams = new CreateContainerParameters
        {
            Labels = labels,
            Name = containerName,
            Image = $"{imageParams.FromImage}:{imageParams.Tag}",
            Env = new List<string>
            {
                $"CCR_ENVOY_DESTINATION_ENDPOINT={envoyDestinationEndpoint}",
                $"CCR_ENVOY_DESTINATION_PORT={envoyDestinationPort}",
                $"CCR_ENVOY_CLUSTER_TYPE=STRICT_DNS",
                $"CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE={DockerConstants.ServiceCertPemFilePath}"
            },
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.EnvoyPort}/tcp", new EmptyStruct()
                }
            },
            Entrypoint = new List<string>
            {
                "/bin/sh",
                "https-http/bootstrap.sh"
            },
            HostConfig = new HostConfig
            {
                Binds = new List<string>
                {
                    $"{hostServiceCertDir}:{DockerConstants.ServiceFolderMountPath}"
                },
                NetworkMode = serviceName,
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{Ports.EnvoyPort}/tcp", new List<PortBinding>
                        {
                            new()
                            {
                                // Dynamic assignment.
                                HostPort = null
                            }
                        }
                    }
                }
            },
        };

        var container = await client.CreateOrGetContainer(createParams);

        await client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        // Fetch again after starting to get the port mapping information.
        container = await client.GetContainerById(container.ID);
        return container.ToEnvoyEndpoint(resourceNameTag);
    }

    public static string GetServiceCertDirectory(string type, string instanceName)
    {
        var infraTypeFolderName = "virtual";
        string wsDir =
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Directory.GetCurrentDirectory();
        return wsDir + $"/{infraTypeFolderName}/service-cert-{type}-{instanceName}";
    }

    public static string GetHostServiceCertDirectory(string type, string instanceName)
    {
        var infraTypeFolderName = "virtual";
        string hostWorkspaceDir =
            Environment.GetEnvironmentVariable("HOST_WORKSPACE_DIR") ??
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ??
            Directory.GetCurrentDirectory();
        return hostWorkspaceDir + $"/{infraTypeFolderName}/service-cert-{type}-{instanceName}";
    }

    public static string GetInsecureVirtualDirectory(string type, string instanceName)
    {
        var infraTypeFolderName = "virtual";
        string wsDir =
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ?? Directory.GetCurrentDirectory();
        return wsDir + $"/{infraTypeFolderName}/insecure-virtual-{type}-{instanceName}";
    }

    public static string GetHostInsecureVirtualDirectory(string type, string instanceName)
    {
        var infraTypeFolderName = "virtual";
        string hostWorkspaceDir =
            Environment.GetEnvironmentVariable("HOST_WORKSPACE_DIR") ??
            Environment.GetEnvironmentVariable("WORKSPACE_DIR") ??
            Directory.GetCurrentDirectory();
        return hostWorkspaceDir + $"/{infraTypeFolderName}/insecure-virtual-{type}-{instanceName}";
    }

    public static string ToProgressMessage(this JSONMessage message)
    {
        var pm = message.Status;
        if (message.Progress != null)
        {
            pm += $" {message.Progress.Current}/{message.Progress.Total}{message.Progress.Units}";
        }

        return pm;
    }

    private static bool IsGitHubActionsEnv()
    {
        return Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
    }
}
