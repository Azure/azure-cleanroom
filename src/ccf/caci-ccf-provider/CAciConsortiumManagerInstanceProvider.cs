// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using Azure.ResourceManager.Resources;
using CcfCommon;
using CcfConsortiumMgrProvider;
using CcfProvider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CAciCcfProvider;

public class CAciConsortiumManagerInstanceProvider : ICcfConsortiumManagerInstanceProvider
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public CAciConsortiumManagerInstanceProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public CMInfraType InfraType => CMInfraType.caci;

    public async Task<CcfConsortiumManagerEndpoint> CreateConsortiumManager(
        string instanceName,
        string consortiumManagerName,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig)
    {
        this.ValidateCreateInput(providerConfig);

        ContainerGroupData? cgData =
            await AciUtils.TryGetContainerGroupData(instanceName, providerConfig!);
        if (cgData != null)
        {
            return this.ToConsortiumManagerEndpoint(cgData);
        }

        string dnsNameLabel =
            this.GenerateDnsName(consortiumManagerName, providerConfig!);
        ContainerGroupData resourceData =
            await this.CreateContainerGroup(
                instanceName,
                consortiumManagerName,
                policyOption,
                providerConfig!,
                dnsNameLabel);
        return this.ToConsortiumManagerEndpoint(resourceData);
    }

    public async Task<CcfConsortiumManagerEndpoint?> TryGetConsortiumManagerEndpoint(
        string consortiumManagerName,
        JsonObject? providerConfig)
    {
        List<ContainerGroupResource> containerGroups =
            await AciUtils.GetConsortiumManagerContainerGroups(
                consortiumManagerName,
                "consortium-manager",
                providerConfig);
        var containerGroup = containerGroups.FirstOrDefault();
        if (containerGroup != null)
        {
            return this.ToConsortiumManagerEndpoint(containerGroup.Data);
        }

        return null;
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

    private async Task<ContainerGroupData> CreateContainerGroup(
        string instanceName,
        string consortiumManagerName,
        SecurityPolicyConfiguration policyOption,
        JsonObject providerConfig,
        string dnsNameLabel)
    {
        var armClient = new ArmClient(new DefaultAzureCredential());
        string location = providerConfig["location"]!.ToString();
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            armClient.GetResourceGroupResource(resourceGroupResourceId);
        ContainerGroupCollection collection =
            resourceGroupResource.GetContainerGroups();

        var containerGroupSecurityPolicy =
            await GetContainerGroupSecurityPolicy();
        ContainerGroupData data =
            CreateContainerGroupData(
                location,
                instanceName,
                consortiumManagerName,
                dnsNameLabel,
                containerGroupSecurityPolicy);

        this.logger.LogInformation(
            $"Starting container group creation for consortium manager: {instanceName}.");
        ArmOperation<ContainerGroupResource> lro =
            await collection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                instanceName,
                data);
        ContainerGroupResource result = lro.Value;

        // The variable result is a resource, you could call other operations on this instance as
        // well.
        ContainerGroupData resourceData = result.Data;

        this.logger.LogInformation(
            $"Container group creation succeeded. " +
            $"Id: {resourceData.Id}, IP address: {resourceData.IPAddress.IP}, " +
            $"Fqdn: {resourceData.IPAddress.Fqdn}.");
        return resourceData;

        async Task<ContainerGroupSecurityPolicy> GetContainerGroupSecurityPolicy()
        {
            this.logger.LogInformation($"policyCreationOption: {policyOption.PolicyCreationOption}");
            if (policyOption.PolicyCreationOption == SecurityPolicyCreationOption.allowAll ||
                policyOption.PolicyCreationOption == SecurityPolicyCreationOption.userSupplied)
            {
                var ccePolicyInput = policyOption.PolicyCreationOption ==
                    SecurityPolicyCreationOption.allowAll ?
                        AciConstants.AllowAllRegoBase64 :
                        policyOption.Policy!;
                return new ContainerGroupSecurityPolicy
                {
                    ConfidentialComputeCcePolicy = ccePolicyInput,
                    Images = new()
                    {
                        {
                            AciConstants.ContainerName.CcfConsortiumManager,
                            $"{ImageUtils.CcfConsortiumManagerImage()}:" +
                            $"{ImageUtils.CcfConsortiumManagerTag()}"
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

            (var policyRego, var policyDocument) =
                await this.DownloadAndExpandPolicy(
                    policyOption.PolicyCreationOption);
            var ccePolicy = Convert.ToBase64String(Encoding.UTF8.GetBytes(policyRego));

            var policyContainers = policyDocument.Containers.ToDictionary(x => x.Name, x => x);
            List<string> requiredContainers =
                [
                    AciConstants.ContainerName.CcfConsortiumManager,
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
            string consortiumManagerName,
            string dnsNameLabel,
            ContainerGroupSecurityPolicy containerGroupSecurityPolicy)
        {
#pragma warning disable MEN002 // Line is too long
            return new ContainerGroupData(
                new AzureLocation(location),
                new ContainerInstanceContainer[]
                {
                    new(
                        AciConstants.ContainerName.CcfConsortiumManager,
                        containerGroupSecurityPolicy.Images[AciConstants.ContainerName.CcfConsortiumManager],
                        new ContainerResourceRequirements(new ContainerResourceRequestsContent(1.5, 1)))
                    {
                        EnvironmentVariables =
                        {
                            new ContainerEnvironmentVariable("ASPNETCORE_URLS")
                            {
                                Value = $"http://+:{Ports.ConsortiumManagerPort}"
                            },
                            new ContainerEnvironmentVariable("SKR_ENDPOINT")
                            {
                                Value = $"http://localhost:{Ports.SkrPort}"
                            },
                        },
                        VolumeMounts =
                        {
                            new ContainerVolumeMount("uds", "/mnt/uds"),
                            new ContainerVolumeMount("certs", MountPaths.CertsFolderMountPath)
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
                            "/bin/bash",
                            "https-http/bootstrap.sh",
                            "--ca-type",
                            "local"
                        },
                        EnvironmentVariables =
                        {
                            new ContainerEnvironmentVariable("CCR_ENVOY_DESTINATION_PORT")
                            {
                                Value = Ports.ConsortiumManagerPort.ToString()
                            },
                            new ContainerEnvironmentVariable("CCR_ENVOY_SERVICE_CERT_OUTPUT_FILE")
                            {
                                Value = MountPaths.ConsortiumManagerCertPemFile
                            }
                        },
                        VolumeMounts =
                        {
                            new ContainerVolumeMount("certs", MountPaths.CertsFolderMountPath)
                        }
                    },
                },
                ContainerInstanceOperatingSystemType.Linux)
            {
                Sku = ContainerGroupSku.Confidential,
                ConfidentialComputeCcePolicy =
                    containerGroupSecurityPolicy.ConfidentialComputeCcePolicy,
                Tags =
                {
                    {
                        AciConstants.CcfConsortiumManagerNameTag,
                        consortiumManagerName
                    },
                    {
                        AciConstants.CcfConsortiumManagerResourceNameTag,
                        instanceName
                    },
                    {
                        AciConstants.CcfConsortiumManagerTypeTag,
                        "consortium-manager"
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
                    AutoGeneratedDomainNameLabelScope = DnsNameLabelReusePolicy.Unsecure
                },
                Volumes =
                {
                    new ContainerVolume("uds")
                    {
                        EmptyDir = BinaryData.FromObjectAsJson(new Dictionary<string, object>())
                    },
                    new ContainerVolume("certs")
                    {
                        EmptyDir = BinaryData.FromObjectAsJson(new Dictionary<string, object>())
                    }
                }
            };
#pragma warning restore MEN002 // Line is too long
        }
    }

    private async Task<(string, SecurityPolicyDocument)> DownloadAndExpandPolicy(
        SecurityPolicyCreationOption policyCreationOption)
    {
        var policyDocument =
            await ImageUtils.GetConsortiumManagerSecurityPolicyDocument(this.logger);

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

    private string GenerateDnsName(string consortiumManagerName, JsonObject providerConfig)
    {
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        string uniqueString =
            Utils.GetUniqueString((subscriptionId + resourceGroupName + consortiumManagerName)
            .ToLower());
        string suffix = "-" + uniqueString;
        string dnsName = consortiumManagerName + suffix;
        if (dnsName.Length > 63)
        {
            // ACI DNS label cannot exceed 63 characters.
            dnsName = dnsName.Substring(0, 63 - suffix.Length) + suffix;
        }

        return dnsName;
    }

    private CcfConsortiumManagerEndpoint ToConsortiumManagerEndpoint(ContainerGroupData cgData)
    {
        if (!cgData.Tags.TryGetValue(
            AciConstants.CcfConsortiumManagerResourceNameTag,
            out var nameTagValue))
        {
            nameTagValue = "NotSet";
        }

        return new CcfConsortiumManagerEndpoint
        {
            Name = nameTagValue,
            Endpoint = $"https://{cgData.IPAddress.Fqdn}:{Ports.EnvoyPort}",
        };
    }
}
