// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfCommon;
using CcfConsortiumMgrProvider;
using CcfProvider;
using CcfRecoveryProvider;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static VirtualCcfProvider.DockerClientEx;

namespace VirtualCcfProvider;

public class DockerConsortiumManagerInstanceProvider : ICcfConsortiumManagerInstanceProvider
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly DockerClient client;

    public DockerConsortiumManagerInstanceProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
    }

    public CMInfraType InfraType => CMInfraType.@virtual;

    public async Task<CcfConsortiumManagerEndpoint> CreateConsortiumManager(
        string instanceName,
        string consortiumManagerName,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        try
        {
            await this.client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = consortiumManagerName
            });
        }
        catch (DockerApiException de) when
        (de.ResponseBody.Contains($"network with name {consortiumManagerName} already exists"))
        {
            // Ignore already exists.
        }

        var credsProxyEndpoint =
            await this.CreateCredentialsProxyContainer(
                instanceName,
                consortiumManagerName);
        var skrEndpoint =
            await this.CreateLocalSkrContainer(
                instanceName,
                consortiumManagerName);
        var envoyEndpoint =
            await this.CreateEnvoyProxyContainer(
                instanceName,
                consortiumManagerName);
        return await
            this.CreateConsortiumManagerContainer(
                instanceName,
                consortiumManagerName,
                skrEndpoint,
                credsProxyEndpoint,
                envoyEndpoint);
    }

    public async Task<CcfConsortiumManagerEndpoint?> TryGetConsortiumManagerEndpoint(
        string consortiumManagerName,
        JsonObject? providerConfig)
    {
        // As envoy fronts the calls return its endpoint details.
        var container = await this.TryGetEnvoyContainer(consortiumManagerName, providerConfig);
        if (container != null)
        {
            var ep = container.ToEnvoyEndpoint(DockerConstants.CcfConsortiumManagerResourceNameTag);
            return this.ToConsortiumManagerEndpoint(ep);
        }

        return null;
    }

    private async Task<CcfConsortiumManagerEndpoint> CreateConsortiumManagerContainer(
        string instanceName,
        string consortiumManagerName,
        string skrEndpoint,
        CredentialsProxyEndpoint credsProxyEndpoint,
        EnvoyEndpoint envoyEndpoint)
    {
        string containerName = "consortium-manager-" + instanceName;
        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.CcfConsortiumManagerImage(),
            Tag = ImageUtils.CcfConsortiumManagerTag(),
        };
        await this.client.Images.CreateImageAsync(
            imageParams,
            authConfig: null,
            new Progress<JSONMessage>(m => this.logger.LogInformation(m.ToProgressMessage())));

        string hostServiceCertDir = DockerClientEx.GetHostServiceCertDirectory("cm", instanceName);

        string hostInsecureVirtualDir =
            DockerClientEx.GetHostInsecureVirtualDirectory("cm", instanceName);
        string insecureVirtualDir =
            DockerClientEx.GetInsecureVirtualDirectory("cm", instanceName);
        Directory.CreateDirectory(insecureVirtualDir);

        // Copy out the test keys/report into the host directory so that it can be mounted into
        // the container.
        WorkspaceDirectories.CopyDirectory(
            Directory.GetCurrentDirectory() + "/insecure-virtual/consortium-manager",
            insecureVirtualDir,
            recursive: true);

        var createParams = new CreateContainerParameters
        {
            Labels = new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfConsortiumManagerNameTag,
                    consortiumManagerName
                },
                {
                    DockerConstants.CcfConsortiumManagerTypeTag,
                    "consortium-manager"
                },
                {
                    DockerConstants.CcfConsortiumManagerResourceNameTag,
                    instanceName
                }
            },
            Name = containerName,
            Image = $"{imageParams.FromImage}:{imageParams.Tag}",
            Env = new List<string>
            {
                $"ASPNETCORE_URLS=http://+:{Ports.ConsortiumManagerPort}",
                $"IDENTITY_ENDPOINT={credsProxyEndpoint.IdentityEndpoint}",
                $"IMDS_ENDPOINT={credsProxyEndpoint.ImdsEndpoint}",
                $"SKR_ENDPOINT={skrEndpoint}",
                $"INSECURE_VIRTUAL_ENVIRONMENT=true"
            },
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.ConsortiumManagerPort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                Binds = new List<string>
                {
                    $"{hostServiceCertDir}:{MountPaths.CertsFolderMountPath}:ro",
                    $"{hostInsecureVirtualDir}:/app/insecure-virtual:ro"
                },
                NetworkMode = consortiumManagerName,

                // Although traffic will be routed via envoy exposing the HTTP port for easier
                // debugging.
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    {
                        $"{Ports.RecoveryServicePort}/tcp", new List<PortBinding>
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

        var container =
            await this.client.CreateOrGetContainer(createParams);
        await this.client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        return this.ToConsortiumManagerEndpoint(envoyEndpoint);
    }

    private async Task<CredentialsProxyEndpoint> CreateCredentialsProxyContainer(
        string instanceName,
        string consortiumManagerName)
    {
        string containerName = "credentials-proxy-" + instanceName;
        return await this.client.CreateCredentialsProxyContainer(
            this.logger,
            containerName,
            consortiumManagerName,
            new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfConsortiumManagerNameTag,
                    consortiumManagerName
                },
                {
                    DockerConstants.CcfConsortiumManagerTypeTag,
                    "credentials-proxy"
                },
                {
                    DockerConstants.CcfConsortiumManagerResourceNameTag,
                    instanceName
                }
            });
    }

    private async Task<string> CreateLocalSkrContainer(
        string instanceName,
        string consortiumManagerName)
    {
        string containerName = "local-skr-" + instanceName;
        return await this.client.CreateLocalSkrContainer(
            this.logger,
            containerName,
            consortiumManagerName,
            new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfConsortiumManagerNameTag,
                    consortiumManagerName
                },
                {
                    DockerConstants.CcfConsortiumManagerTypeTag,
                    "local-skr"
                },
                {
                    DockerConstants.CcfConsortiumManagerResourceNameTag,
                    instanceName
                }
            });
    }

    private Task<EnvoyEndpoint> CreateEnvoyProxyContainer(
        string instanceName,
        string consortiumManagerName)
    {
        string containerName = "envoy-" + instanceName;
        string serviceCertDir = DockerClientEx.GetServiceCertDirectory("cm", instanceName);
        string hostServiceCertDir =
            DockerClientEx.GetHostServiceCertDirectory("cm", instanceName);

        // Create the scratch directory that gets mounted into the envoy container which then
        // writes out the service cert pem file in this location. The recovery service container
        // reads this file and serves it out via the /report endpoint.
        Directory.CreateDirectory(serviceCertDir);

        string envoyDestinationEndpoint = "consortium-manager-" + instanceName;
        return this.client.CreateEnvoyProxyContainer(
            this.logger,
            envoyDestinationEndpoint,
            Ports.ConsortiumManagerPort,
            containerName,
            consortiumManagerName,
            hostServiceCertDir,
            MountPaths.ConsortiumManagerCertPemFile,
            DockerConstants.CcfConsortiumManagerResourceNameTag,
            new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfConsortiumManagerNameTag,
                    consortiumManagerName
                },
                {
                    DockerConstants.CcfConsortiumManagerTypeTag,
                    "ccr-proxy"
                },
                {
                    DockerConstants.CcfConsortiumManagerResourceNameTag,
                    instanceName
                }
            });
    }

    private async Task<ContainerListResponse?> TryGetEnvoyContainer(
        string consortiumManagerName,
        JsonObject? providerConfig)
    {
        var containers = await this.client.GetContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label",
                new Dictionary<string, bool>
                {
                    {
                        $"{DockerConstants.CcfConsortiumManagerNameTag}={consortiumManagerName}",
                        true
                    },
                    {
                        $"{DockerConstants.CcfConsortiumManagerTypeTag}=ccr-proxy",
                        true
                    }
                }
            }
        });

        return containers.FirstOrDefault();
    }

    private CcfConsortiumManagerEndpoint ToConsortiumManagerEndpoint(EnvoyEndpoint ep)
    {
        // As envoy will front the calls return its endpoint.
        return new CcfConsortiumManagerEndpoint
        {
            Name = ep.Name,
            Endpoint = ep.Endpoint
        };
    }
}
