// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcfCommon;
using CcfProvider;
using CcfRecoveryProvider;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCcfProvider;

public class DockerRecoveryServiceInstanceProvider : ICcfRecoveryServiceInstanceProvider
{
    private const string ProviderFolderName = "virtual";

    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly DockerClient client;

    public DockerRecoveryServiceInstanceProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.client = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
    }

    public RsInfraType InfraType => RsInfraType.@virtual;

    public async Task<RecoveryServiceEndpoint> CreateRecoveryService(
        string instanceName,
        string serviceName,
        string akvEndpoint,
        string maaEndpoint,
        string? managedIdentityId,
        NetworkJoinPolicy networkJoinPolicy,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        try
        {
            await this.client.Networks.CreateNetworkAsync(new NetworksCreateParameters
            {
                Name = serviceName
            });
        }
        catch (DockerApiException de) when
        (de.ResponseBody.Contains($"network with name {serviceName} already exists"))
        {
            // Ignore already exists.
        }

        var credsProxyEndpoint = await this.CreateCredentialsProxyContainer(
            instanceName,
            serviceName,
            providerConfig);
        var skrEndpoint = await this.CreateLocalSkrContainer(
            instanceName,
            serviceName,
            providerConfig);
        var envoyEndpoint = await this.CreateEnvoyProxyContainer(
            instanceName,
            serviceName,
            providerConfig);
        return await this.CreateRecoveryServiceContainer(
            instanceName,
            serviceName,
            akvEndpoint,
            maaEndpoint,
            networkJoinPolicy,
            providerConfig,
            skrEndpoint,
            credsProxyEndpoint,
            envoyEndpoint);
    }

    public async Task DeleteRecoveryService(string serviceName, JsonObject? providerConfig)
    {
        await this.client.DeleteContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfRecoveryServiceNameTag}={serviceName}", true },
                }
            }
        });
    }

    public async Task<RecoveryServiceEndpoint> GetRecoveryServiceEndpoint(
        string serviceName,
        JsonObject? providerConfig)
    {
        return await this.TryGetRecoveryServiceEndpoint(serviceName, providerConfig) ??
            throw new Exception($"No recovery service endpoint found for {serviceName}.");
    }

    public async Task<RecoveryServiceEndpoint?> TryGetRecoveryServiceEndpoint(
        string serviceName,
        JsonObject? providerConfig)
    {
        // As envoy fronts the calls return its endpoint details.
        var container = await this.TryGetEnvoyContainer(serviceName, providerConfig);
        if (container != null)
        {
            var ep = container.ToEnvoyEndpoint(DockerConstants.CcfRecoveryServiceResourceNameTag);
            return this.ToServiceEndpoint(ep);
        }

        return null;
    }

    public Task<JsonObject> GenerateSecurityPolicy(
        NetworkJoinPolicy joinPolicy,
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

    public async Task<RecoveryServiceHealth> GetRecoveryServiceHealth(
        string serviceName,
        JsonObject? providerConfig)
    {
        var svcContainer = await this.TryGetRecoveryServiceContainer(serviceName, providerConfig);
        if (svcContainer == null)
        {
            throw new Exception($"No recovery service endpoint found for {serviceName}.");
        }

        var ep = await this.TryGetRecoveryServiceEndpoint(serviceName, providerConfig);
        if (ep == null)
        {
            throw new Exception($"No envoy endpoint found for {serviceName}.");
        }

        var health = this.ToRecoveryServiceHealth(svcContainer);
        health.Name = ep.Name;
        health.Endpoint = ep.Endpoint;
        return health;
    }

    private async Task<RecoveryServiceEndpoint> CreateRecoveryServiceContainer(
        string instanceName,
        string serviceName,
        string akvEndpoint,
        string maaEndpoint,
        NetworkJoinPolicy networkJoinPolicy,
        JsonObject? providerConfig,
        string skrEndpoint,
        CredentialsProxyEndpoint credsProxyEndpoint,
        EnvoyEndpoint envoyEndpoint)
    {
        string containerName = "recovery-service-" + instanceName;
        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.CcfRecoveryServiceImage(),
            Tag = ImageUtils.CcfRecoveryServiceTag(),
        };
        await this.client.Images.CreateImageAsync(
            imageParams,
            authConfig: null,
            new Progress<JSONMessage>(m => this.logger.LogInformation(m.ToProgressMessage())));

        string hostServiceCertDir = DockerClientEx.GetHostServiceCertDirectory("rs", instanceName);

        string hostInsecureVirtualDir =
            DockerClientEx.GetHostInsecureVirtualDirectory("rs", instanceName);
        string insecureVirtualDir =
            DockerClientEx.GetInsecureVirtualDirectory("rs", instanceName);
        Directory.CreateDirectory(insecureVirtualDir);

        // Copy out the test keys/report into the host directory so that it can be mounted into
        // the container.
        WorkspaceDirectories.CopyDirectory(
            Directory.GetCurrentDirectory() + "/insecure-virtual/recovery-service",
            insecureVirtualDir,
            recursive: true);

        string base64EncodedPolicy =
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(networkJoinPolicy)));
        var createParams = new CreateContainerParameters
        {
            Labels = new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfRecoveryServiceNameTag,
                    serviceName
                },
                {
                    DockerConstants.CcfRecoveryServiceTypeTag,
                    "recovery-service"
                },
                {
                    DockerConstants.CcfRecoveryServiceResourceNameTag,
                    instanceName
                }
            },
            Name = containerName,
            Image = $"{imageParams.FromImage}:{imageParams.Tag}",
            Env = new List<string>
            {
                $"ASPNETCORE_URLS=http://+:{Ports.RecoveryServicePort}",
                $"IDENTITY_ENDPOINT={credsProxyEndpoint.IdentityEndpoint}",
                $"IMDS_ENDPOINT={credsProxyEndpoint.ImdsEndpoint}",
                $"AKV_ENDPOINT={akvEndpoint}",
                $"MAA_ENDPOINT={maaEndpoint}",
                $"SKR_ENDPOINT={skrEndpoint}",
                $"CCF_NETWORK_INITIAL_JOIN_POLICY={base64EncodedPolicy}",
                $"SERVICE_CERT_LOCATION={DockerConstants.ServiceCertPemFilePath}",
                $"INSECURE_VIRTUAL_ENVIRONMENT=true"
            },
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.RecoveryServicePort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                Binds = new List<string>
                {
                    $"{hostServiceCertDir}:{DockerConstants.ServiceFolderMountPath}:ro",
                    $"{hostInsecureVirtualDir}:/app/insecure-virtual:ro"
                },
                NetworkMode = serviceName,

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
        var container = await this.client.CreateOrGetContainer(createParams);

        await this.client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        return this.ToServiceEndpoint(envoyEndpoint);
    }

    private async Task<ContainerListResponse?> TryGetEnvoyContainer(
        string serviceName,
        JsonObject? providerConfig)
    {
        var containers = await this.client.GetContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfRecoveryServiceNameTag}={serviceName}", true },
                    { $"{DockerConstants.CcfRecoveryServiceTypeTag}=ccr-proxy", true }
                }
            }
        });

        return containers.FirstOrDefault();
    }

    private async Task<ContainerListResponse?> TryGetRecoveryServiceContainer(
        string serviceName,
        JsonObject? providerConfig)
    {
        var containers = await this.client.GetContainers(
            this.logger,
            filters: new Dictionary<string, IDictionary<string, bool>>
        {
            {
                "label", new Dictionary<string, bool>
                {
                    { $"{DockerConstants.CcfRecoveryServiceNameTag}={serviceName}", true },
                    { $"{DockerConstants.CcfRecoveryServiceTypeTag}=recovery-service", true }
                }
            }
        });

        return containers.FirstOrDefault();
    }

    private RecoveryServiceEndpoint ToServiceEndpoint(EnvoyEndpoint ep)
    {
        // As envoy will front the calls return its endpoint as the svc endpoint.
        return new RecoveryServiceEndpoint
        {
            Name = ep.Name,
            Endpoint = ep.Endpoint
        };
    }

    private async Task<CredentialsProxyEndpoint> CreateCredentialsProxyContainer(
        string instanceName,
        string serviceName,
        JsonObject? providerConfig)
    {
        string containerName = "credentials-proxy-" + instanceName;
        string? user = Environment.GetEnvironmentVariable("CREDENTIALS_PROXY_USER");
        string? hostVolumePath = Environment.GetEnvironmentVariable(
            "CREDENTIALS_PROXY_HOST_AZURE_VOLUME");
        if (string.IsNullOrEmpty(hostVolumePath))
        {
            throw new ArgumentException("CREDENTIALS_PROXY_HOST_AZURE_VOLUME is not set.");
        }

        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.CredentialsProxyImage(),
            Tag = ImageUtils.CredentialsProxyTag(),
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
                    DockerConstants.CcfRecoveryServiceNameTag,
                    serviceName
                },
                {
                    DockerConstants.CcfRecoveryServiceTypeTag,
                    "credentials-proxy"
                },
                {
                    DockerConstants.CcfRecoveryServiceResourceNameTag,
                    instanceName
                }
            },
            Name = containerName,
            Image = $"{imageParams.FromImage}:{imageParams.Tag}",
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.CredentialsProxyPort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = serviceName
            },
        };

        if (!string.IsNullOrEmpty(user))
        {
            createParams.User = user;
        }

        createParams.HostConfig.Binds = new List<string>
        {
            $"{hostVolumePath}:/app/.azure"
        };

        var container = await this.client.CreateOrGetContainer(createParams);

        await this.client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        return new CredentialsProxyEndpoint
        {
            IdentityEndpoint = $"http://{containerName}:8080/token",
            ImdsEndpoint = "dummy_required_value"
        };
    }

    private RecoveryServiceHealth ToRecoveryServiceHealth(ContainerListResponse container)
    {
        var status = CcfRecoveryProvider.ServiceStatus.Ok;
        var reasons = new List<CcfRecoveryProvider.Reason>();
        if (container.State == "exited")
        {
            status = CcfRecoveryProvider.ServiceStatus.Unhealthy;
            var code = "ContainerExited";
            var message = $"Container {container.ID} has exited: {container.Status}.";
            reasons.Add(new() { Code = code, Message = message });
        }

        return new RecoveryServiceHealth
        {
            Status = status.ToString(),
            Reasons = reasons
        };
    }

    private async Task<string> CreateLocalSkrContainer(
        string instanceName,
        string serviceName,
        JsonObject? providerConfig)
    {
        string containerName = "local-skr-" + instanceName;

        var imageParams = new ImagesCreateParameters
        {
            FromImage = ImageUtils.LocalSkrImage(),
            Tag = ImageUtils.LocalSkrTag(),
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
                    DockerConstants.CcfRecoveryServiceNameTag,
                    serviceName
                },
                {
                    DockerConstants.CcfRecoveryServiceTypeTag,
                    "local-skr"
                },
                {
                    DockerConstants.CcfRecoveryServiceResourceNameTag,
                    instanceName
                }
            },
            Name = containerName,
            Image = $"{imageParams.FromImage}:{imageParams.Tag}",
            ExposedPorts = new Dictionary<string, EmptyStruct>
            {
                {
                    $"{Ports.SkrPort}/tcp", new EmptyStruct()
                }
            },
            HostConfig = new HostConfig
            {
                NetworkMode = serviceName
            },
        };

        var container = await this.client.CreateOrGetContainer(createParams);

        await this.client.Containers.StartContainerAsync(
            container.ID,
            new ContainerStartParameters());

        return $"http://{containerName}:{Ports.SkrPort}";
    }

    private Task<EnvoyEndpoint> CreateEnvoyProxyContainer(
        string instanceName,
        string serviceName,
        JsonObject? providerConfig)
    {
        string containerName = "envoy-" + instanceName;
        string serviceCertDir = DockerClientEx.GetServiceCertDirectory("rs", instanceName);
        string hostServiceCertDir =
            DockerClientEx.GetHostServiceCertDirectory("rs", instanceName);

        // Create the scratch directory that gets mounted into the envoy container which then
        // writes out the service cert pem file in this location. The recovery service container
        // reads this file and serves it out via the /report endpoint.
        Directory.CreateDirectory(serviceCertDir);

        string envoyDestinationEndpoint = "recovery-service-" + instanceName;
        return this.client.CreateEnvoyProxyContainer(
            this.logger,
            envoyDestinationEndpoint,
            Ports.RecoveryServicePort,
            containerName,
            serviceName,
            hostServiceCertDir,
            DockerConstants.CcfRecoveryServiceResourceNameTag,
            new Dictionary<string, string>
            {
                {
                    DockerConstants.CcfRecoveryServiceNameTag,
                    serviceName
                },
                {
                    DockerConstants.CcfRecoveryServiceTypeTag,
                    "ccr-proxy"
                },
                {
                    DockerConstants.CcfRecoveryServiceResourceNameTag,
                    instanceName
                }
            });
    }

    public class CredentialsProxyEndpoint
    {
        public string IdentityEndpoint { get; set; } = default!;

        public string ImdsEndpoint { get; set; } = default!;
    }

    public class EnvoyEndpoint
    {
        public string Name { get; set; } = default!;

        public string Endpoint { get; set; } = default!;
    }
}
