// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Resources;
using CcfCommon;
using CcfProvider;
using CcfRecoveryProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCcfProvider;

public class CAciRecoveryServiceInstanceProvider : ICcfRecoveryServiceInstanceProvider
{
    private const string ServiceFolderMountPath = "/app/service";
    private const string ServiceCertPemFilePath = $"{ServiceFolderMountPath}/service-cert.pem";
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public CAciRecoveryServiceInstanceProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public RsInfraType InfraType => RsInfraType.caci;

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
        this.ValidateCreateInput(providerConfig);

        string containerGroupName = instanceName;
        ContainerGroupData? cgData =
            await AciUtils.TryGetContainerGroupData(containerGroupName, providerConfig!);
        if (cgData != null)
        {
            return this.ToServiceEndpoint(cgData);
        }

        return await this.CreateRecoveryServiceInstanceContainerGroup(
            instanceName,
            serviceName,
            akvEndpoint,
            maaEndpoint,
            managedIdentityId,
            networkJoinPolicy,
            policyOption,
            providerConfig);
    }

    public async Task DeleteRecoveryService(string serviceName, JsonObject? providerConfig)
    {
        this.ValidateDeleteInput(providerConfig);

        List<ContainerGroupResource> lbContainerGroups =
            await AciUtils.GetRecoveryServiceContainerGroups(
                serviceName,
                "recovery-service",
                providerConfig);

        this.logger.LogInformation(
            $"Found {lbContainerGroups.Count} recovery service container groups to delete.");
        foreach (var resource in lbContainerGroups)
        {
            this.logger.LogInformation($"Deleting recovery service container group {resource.Id}");
            await resource.DeleteAsync(WaitUntil.Completed);
        }
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
        List<ContainerGroupResource> lbContainerGroups =
            await AciUtils.GetRecoveryServiceContainerGroups(
                serviceName,
                "recovery-service",
                providerConfig);
        var lbContainerGroup = lbContainerGroups.FirstOrDefault();
        if (lbContainerGroup != null)
        {
            return this.ToServiceEndpoint(lbContainerGroup.Data);
        }

        return null;
    }

    public async Task<JsonObject> GenerateSecurityPolicy(
        NetworkJoinPolicy joinPolicy,
        SecurityPolicyCreationOption policyOption)
    {
        string policyRego;
        if (policyOption == SecurityPolicyCreationOption.allowAll)
        {
            policyRego = Encoding.UTF8.GetString(Convert.FromBase64String(
                AciConstants.AllowAllRegoBase64));
        }
        else
        {
            string base64EncodedJoinPolicy = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(joinPolicy)));
            (policyRego, _) = await this.DownloadAndExpandPolicy(
                policyOption,
                base64EncodedJoinPolicy);
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

    private async Task<RecoveryServiceEndpoint> CreateRecoveryServiceInstanceContainerGroup(
        string instanceName,
        string serviceName,
        string akvEndpoint,
        string maaEndpoint,
        string? managedIdentityId,
        NetworkJoinPolicy networkJoinPolicy,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        this.ValidateCreateInput(providerConfig);

        string dnsNameLabel = this.GenerateDnsName(serviceName, providerConfig!);

        ContainerGroupData resourceData = await this.CreateContainerGroup(
            instanceName,
            serviceName,
            akvEndpoint,
            maaEndpoint,
            managedIdentityId!,
            networkJoinPolicy,
            policyOption,
            providerConfig!,
            dnsNameLabel);

        return this.ToServiceEndpoint(resourceData);
    }

    private async Task<ContainerGroupData> CreateContainerGroup(
        string instanceName,
        string serviceName,
        string akvEndpoint,
        string maaEndpoint,
        string managedIdentityId,
        NetworkJoinPolicy networkJoinPolicy,
        SecurityPolicyConfiguration policyOption,
        JsonObject providerConfig,
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

        string base64EncodedJoinPolicy = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(networkJoinPolicy)));

        var containerGroupSecurityPolicy = await GetContainerGroupSecurityPolicy();

        ContainerGroupData data = CreateContainerGroupData(
            location,
            instanceName,
            serviceName,
            akvEndpoint,
            maaEndpoint,
            managedIdentityId,
            networkJoinPolicy,
            dnsNameLabel,
            containerGroupSecurityPolicy);

        this.logger.LogInformation(
            $"Starting container group creation for recovery service: {instanceName}");

        ArmOperation<ContainerGroupResource> lro = await collection.CreateOrUpdateAsync(
            WaitUntil.Completed,
            instanceName,
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

        async Task<ContainerGroupSecurityPolicy> GetContainerGroupSecurityPolicy()
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
                        AciConstants.ContainerName.CcfRecoveryService,
                        $"{ImageUtils.CcfRecoveryServiceImage()}:" +
                        $"{ImageUtils.CcfRecoveryServiceTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrAttestation,
                        $"{ImageUtils.CcrAttestationImage()}:{ImageUtils.CcrAttestationTag()}"
                    },
                    {
                        AciConstants.ContainerName.Skr,
                        $"{ImageUtils.SkrImage()}:{ImageUtils.SkrTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrProxy,
                        $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}"
                    }
                }
                };
            }

            (var policyRego, var policyDocument) = await this.DownloadAndExpandPolicy(
                policyOption.PolicyCreationOption,
                base64EncodedJoinPolicy);

            var ccePolicy = Convert.ToBase64String(Encoding.UTF8.GetBytes(policyRego));

            var policyContainers = policyDocument.Containers.ToDictionary(x => x.Name, x => x);
            List<string> requiredContainers =
                [
                    AciConstants.ContainerName.CcfRecoveryService,
                    AciConstants.ContainerName.CcrAttestation,
                    AciConstants.ContainerName.Skr,
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

        ContainerGroupData CreateContainerGroupData(
            string location,
            string instanceName,
            string serviceName,
            string akvEndpoint,
            string maaEndpoint,
            string managedIdentityId,
            NetworkJoinPolicy? networkJoinPolicy,
            string dnsNameLabel,
            ContainerGroupSecurityPolicy containerGroupSecurityPolicy)
        {
#pragma warning disable MEN002 // Line is too long
            return new ContainerGroupData(
                new AzureLocation(location),
                new ContainerInstanceContainer[]
                {
                new(
                    AciConstants.ContainerName.CcfRecoveryService,
                    containerGroupSecurityPolicy.Images[AciConstants.ContainerName.CcfRecoveryService],
                    new ContainerResourceRequirements(new ContainerResourceRequestsContent(1.5, 1)))
                    {
                        EnvironmentVariables =
                        {
                            new ContainerEnvironmentVariable("ASPNETCORE_URLS")
                            {
                                Value = $"http://+:{Ports.RecoveryServicePort}"
                            },
                            new ContainerEnvironmentVariable("AKV_ENDPOINT")
                            {
                                Value = akvEndpoint
                            },
                            new ContainerEnvironmentVariable("MAA_ENDPOINT")
                            {
                                Value = maaEndpoint
                            },
                            new ContainerEnvironmentVariable("SKR_ENDPOINT")
                            {
                                Value = $"http://localhost:{Ports.SkrPort}"
                            },
                            new ContainerEnvironmentVariable("SERVICE_CERT_LOCATION")
                            {
                                Value = ServiceCertPemFilePath
                            },
                            new ContainerEnvironmentVariable("CCF_NETWORK_INITIAL_JOIN_POLICY")
                            {
                                Value = base64EncodedJoinPolicy
                            }
                        },
                        VolumeMounts =
                        {
                            new ContainerVolumeMount("uds", "/mnt/uds"),
                            new ContainerVolumeMount("shared", "/app/service")
                        }
                    },
                new(
                    AciConstants.ContainerName.CcrAttestation,
                    containerGroupSecurityPolicy.Images[AciConstants.ContainerName.CcrAttestation],
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
                    AciConstants.ContainerName.Skr,
                    containerGroupSecurityPolicy.Images[AciConstants.ContainerName.Skr],
                    new ContainerResourceRequirements(
                        new ContainerResourceRequestsContent(0.5, 0.2)))
                    {
                        Command =
                        {
                            "/skr.sh"
                        },
                        EnvironmentVariables =
                        {
                            new ContainerEnvironmentVariable("SkrSideCarArgs")
                            {
                                Value = "ewogICAiY2VydGNhY2hlIjogewogICAgICAiZW5kcG9pbnQiOiAiYW1lcmljYXMuYWNjY2FjaGUuYXp1cmUubmV0IiwKICAgICAgInRlZV90eXBlIjogIlNldlNucFZNIiwKICAgICAgImFwaV92ZXJzaW9uIjogImFwaS12ZXJzaW9uPTIwMjAtMTAtMTUtcHJldmlldyIKICAgfQp9"
                            },
                            new ContainerEnvironmentVariable("Port")
                            {
                                Value = $"{Ports.SkrPort}"
                            },
                            new ContainerEnvironmentVariable("LogLevel")
                            {
                                Value = "Info"
                            },
                            new ContainerEnvironmentVariable("LogFile")
                            {
                                Value = "skr.log"
                            }
                        }
                    },
                new(
                    AciConstants.ContainerName.CcrProxy,
                    containerGroupSecurityPolicy.Images[AciConstants.ContainerName.CcrProxy],
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
                            Value = Ports.RecoveryServicePort.ToString()
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
                Sku = ContainerGroupSku.Confidential,
                ConfidentialComputeCcePolicy =
                    containerGroupSecurityPolicy.ConfidentialComputeCcePolicy,
                Identity = new ManagedServiceIdentity(ManagedServiceIdentityType.UserAssigned)
                {
                    UserAssignedIdentities =
                    {
                        {
                            new ResourceIdentifier(managedIdentityId),
                            new UserAssignedIdentity()
                        }
                    }
                },
                Tags =
                {
                    {
                        AciConstants.CcfRecoveryServiceNameTag,
                        serviceName
                    },
                    {
                        AciConstants.CcfRecoveryServiceTypeTag,
                        "recovery-service"
                    },
                    {
                        AciConstants.CcfRecoveryServiceResourceNameTag,
                        instanceName
                    }
                },
                IPAddress = new ContainerGroupIPAddress(
                    new ContainerGroupPort[]
                    {
                        new(Ports.EnvoyPort)
                        {
                            Protocol = ContainerGroupNetworkProtocol.Tcp,
                        }
                    },
                    ContainerGroupIPAddressType.Public)
                {
                    DnsNameLabel = dnsNameLabel,

                    // TODO (gsinha): Come up with deterministic naming scheme or use an enum value.
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
#pragma warning restore MEN002 // Line is too long
        }
    }

    private async Task<(string, SecurityPolicyDocument)> DownloadAndExpandPolicy(
        SecurityPolicyCreationOption policyCreationOption,
        string base64EncodedJoinPolicy)
    {
        var policyDocument =
            await ImageUtils.GetRecoveryServiceSecurityPolicyDocument(this.logger);

        foreach (var container in policyDocument.Containers)
        {
            container.Image = container.Image.Replace("@@RegistryUrl@@", ImageUtils.RegistryUrl());
        }

        var policyRego =
            policyCreationOption == SecurityPolicyCreationOption.cachedDebug ?
            policyDocument.RegoDebug :
            policyCreationOption == SecurityPolicyCreationOption.cached ? policyDocument.Rego :
                throw new ArgumentException($"Unexpected option: {policyCreationOption}");

        // Replace placeholder variables in the policy.
        policyRego = policyRego.Replace("$CcfNetworkInitialJoinPolicy", base64EncodedJoinPolicy);
        return (policyRego, policyDocument);
    }

    private string GenerateDnsName(string serviceName, JsonObject providerConfig)
    {
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        string uniqueString =
            Utils.GetUniqueString((subscriptionId + resourceGroupName + serviceName).ToLower());
        string suffix = "-" + uniqueString;
        string dnsName = serviceName + suffix;
        if (dnsName.Length > 63)
        {
            // ACI DNS label cannot exceed 63 characters.
            dnsName = dnsName.Substring(0, 63 - suffix.Length) + suffix;
        }

        return dnsName;
    }

    private RecoveryServiceEndpoint ToServiceEndpoint(ContainerGroupData cgData)
    {
        if (!cgData.Tags.TryGetValue(
            AciConstants.CcfRecoveryServiceResourceNameTag,
            out var nameTagValue))
        {
            nameTagValue = "NotSet";
        }

        return new RecoveryServiceEndpoint
        {
            Name = nameTagValue,
            Endpoint = $"https://{cgData.IPAddress.Fqdn}:{Ports.EnvoyPort}",
        };
    }
}
