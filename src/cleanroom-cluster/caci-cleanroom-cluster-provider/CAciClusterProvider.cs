// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.Authorization.Models;
using Azure.ResourceManager.ContainerService;
using Azure.ResourceManager.ContainerService.Models;
using Azure.ResourceManager.ManagedServiceIdentities;
using Azure.ResourceManager.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.PrivateDns;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using CleanRoomProvider;
using Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CAciCleanRoomProvider;

public class CAciClusterProvider : ICleanRoomClusterProvider
{
    private const string CleanRoomClusterTag = "azcleanroom-cluster:cluster-name";
    private const string AgentPoolName = "agentpool";
    private const string AksSubnetName = "aks";
    private const string AciSubnetName = "cg";
    private const string ExternalDnsWorkloadMiName = "external-dns-identity";

    private HttpClientManager httpClientManager;
    private ILogger logger;
    private IConfiguration configuration;

    public CAciClusterProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.httpClientManager = new(logger);
    }

    public InfraType InfraType => InfraType.caci;

    public ODataError? CreateClusterValidate(
        string clClusterName,
        JsonObject? providerConfig)
    {
        return ValidateInput(providerConfig);
    }

    public ODataError? GetClusterValidate(
        string clClusterName,
        JsonObject? providerConfig)
    {
        return ValidateInput(providerConfig);
    }

    public ODataError? DeleteClusterValidate(
        string clClusterName,
        JsonObject? providerConfig)
    {
        return ValidateInput(providerConfig);
    }

    public async Task<CleanRoomCluster> CreateCluster(
        string clClusterName,
        CleanRoomClusterInput input,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string location = providerConfig!["location"]!.ToString();
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        _ = bool.TryParse(providerConfig["forceCreate"]?.ToString(), out bool forceCreate);

        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        // VN2 requires creation of a VNET with default/aks/cg subnets. So create an AKS cluster
        // attached to such a vnet.
        // cluster.
        progressReporter.Report("Creating vnet...");
        var vnet = await CreateVnetAsync(location, resourceGroupResource, clClusterName);
        progressReporter.Report("Creating aks cluster...");
        var aks = await CreateAksClusterAsync(location, resourceGroupResource, clClusterName, vnet);

        // Update AKS agent pool MI to have permissions to create ACI container groups in the MC
        // resource group and to inject container groups in the vnet.
        await UpdateAgentPoolMiPermissionsOnMCRgAsync(aks);
        await UpdateAgentPoolMiPermissionsOnVNetRgAsync(aks, vnet);

        string kubeConfigFile = await GetKubeConfigFile(aks);
        progressReporter.Report("Installing VN2...");
        await this.InstallVN2OnAksAsync(aks, kubeConfigFile);
        progressReporter.Report("Installing spark operator...");
        await this.InstallSparkOperatorOnAksAsync(aks, kubeConfigFile);

        // In some cases the workload identity deployment takes a significant amount of time
        // causing failures further downstream. To avoid this, wait for the deployment to be
        // ready.
        await this.WaitForWorkloadIdentityDeploymentUp(kubeConfigFile);

        progressReporter.Report("Installing external-dns...");
        var workloadMi = await this.InstallExternalDnsOnAksAsync(
            resourceGroupResource,
            clClusterName,
            aks,
            providerConfig,
            kubeConfigFile);

        await this.UpdateMiPermissionsForPrivateDnsZoneAsync(resourceGroupResource, workloadMi);

        _ = bool.TryParse(providerConfig["noWaitOnReady"]?.ToString(), out bool noWaitOnReady);

        if (input.ObservabilityProfile != null && input.ObservabilityProfile.Enabled)
        {
            await this.EnableClusterObservabilityAsync(
                client,
                aks,
                resourceGroupResource,
                vnet,
                kubeConfigFile,
                clClusterName,
                forceCreate,
                noWaitOnReady,
                progressReporter);
        }

        if (input.AnalyticsWorkloadProfile != null && input.AnalyticsWorkloadProfile.Enabled)
        {
            await this.EnableAnalyticsWorkloadAsync(
                client,
                clClusterName,
                resourceGroupResource,
                kubeConfigFile,
                aks,
                vnet,
                input.AnalyticsWorkloadProfile,
                forceCreate,
                noWaitOnReady,
                progressReporter);
        }

        // Wait for spark-operator pod/deployment to be ready or else clients might try to submit
        // spark jobs but see failures like:
        // Internal error occurred: failed calling webhook
        // "mutate-sparkoperator-k8s-io-v1beta2-sparkapplication.sparkoperator.k8s.io":
        // failed to call webhook: Post "
        // https://spark-operator-webhook-svc.spark-operator.svc:9443/mutate-sparkoperator-k8s-io-v1beta2-sparkapplication?timeout=10s":
        // dial tcp 10.96.244.127:9443: connect: connection refused.
        progressReporter.Report("Waiting for spark-operator to become ready...");
        await this.WaitForSparkOperatorUp(kubeConfigFile);

        // Also wait for external-dns pod/deployments to be up to catch any issues.
        progressReporter.Report("Waiting for external-dns to become ready...");
        await this.WaitForExternalDnsUp(kubeConfigFile);

        progressReporter.Report("Cluster creation completed.");
        this.logger.LogInformation($"Cluster creation completed: {clClusterName}");
        return await this.GetCluster(clClusterName, providerConfig);

        async Task<VirtualNetworkResource> CreateVnetAsync(
            string location,
            ResourceGroupResource resourceGroupResource,
            string clClusterName)
        {
            var vnetName = this.ToVnetName(clClusterName);

            if (!forceCreate)
            {
                try
                {
                    var vnet = await resourceGroupResource.GetVirtualNetworkAsync(vnetName);
                    this.logger.LogInformation(
                        $"Found existing virtual network so skipping creation: {vnetName}");
                    return vnet;
                }
                catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
                {
                    // Does not exist. Proceed to creation.
                }
            }

            var natGateway = await CreateNatGatewayForCgSubnetEgressAsync();

            VirtualNetworkCollection collection = resourceGroupResource.GetVirtualNetworks();
            this.logger.LogInformation(
                $"Starting virtual network creation: {vnetName}");
            VirtualNetworkData data = new()
            {
                Tags =
                {
                    {
                        CleanRoomClusterTag,
                        clClusterName
                    }
                },
                Location = new AzureLocation(location),
                AddressSpace = new()
                {
                    AddressPrefixes =
                {
                    "10.0.0.0/16",
                    "10.1.0.0/16",
                    "10.2.0.0/16"
                }
                },
                Subnets =
            {
                new SubnetData
                {
                    Name = "default",
                    AddressPrefixes = { "10.0.0.0/24" }
                },
                new SubnetData()
                {
                    Name = AksSubnetName,
                    AddressPrefixes = { "10.1.0.0/16" }
                },
                new SubnetData()
                {
                    Name = AciSubnetName,
                    AddressPrefixes = { "10.2.0.0/16" },
                    Delegations =
                    {
                        new Azure.ResourceManager.Network.Models.ServiceDelegation
                        {
                            Name = "Microsoft.ContainerInstance/containerGroups",
                            ServiceName = "Microsoft.ContainerInstance/containerGroups"
                        }
                    },
                    NatGatewayId = natGateway.Data.Id
                }
            }
            };

            ArmOperation<VirtualNetworkResource> lro = await collection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                vnetName,
                data);
            VirtualNetworkResource result = lro.Value;
            VirtualNetworkData resourceData = result.Data;

            this.logger.LogInformation(
                $"virtual network creation succeeded. id: {resourceData.Id}");
            return result;

            async Task<NatGatewayResource> CreateNatGatewayForCgSubnetEgressAsync()
            {
                // https://learn.microsoft.com/en-us/azure/container-instances/container-instances-nat-gateway
                var ipName = "cg-egress-ip";
                var gatewayName = "cg-nat-gateway";

                var publicIP = await this.CreatePublicIP(
                    resourceGroupResource,
                    location,
                    clClusterName,
                    ipName,
                    forceCreate);

                return await CreateNatGatewayAsync();

                async Task<NatGatewayResource> CreateNatGatewayAsync()
                {
                    if (!forceCreate)
                    {
                        try
                        {
                            var natGateway = await resourceGroupResource.GetNatGatewayAsync(
                                gatewayName);
                            this.logger.LogInformation(
                                $"Found existing nat gateway so skipping creation: {gatewayName}");
                            return natGateway;
                        }
                        catch (RequestFailedException rfe)
                        when (rfe.Status == (int)HttpStatusCode.NotFound)
                        {
                            // Does not exist. Proceed to creation.
                        }
                    }

                    NatGatewayCollection collection = resourceGroupResource.GetNatGateways();
                    this.logger.LogInformation(
                        $"Starting nat gateway creation: {gatewayName}");
                    NatGatewayData data = new()
                    {
                        Tags =
                    {
                        {
                            CleanRoomClusterTag,
                            clClusterName
                        }
                    },
                        Location = new AzureLocation(location),
                        SkuName = NatGatewaySkuName.Standard,
                        IdleTimeoutInMinutes = 10,
                        PublicIPAddresses =
                    {
                        new WritableSubResource() { Id = publicIP.Id }
                    }
                    };

                    ArmOperation<NatGatewayResource> lro = await collection.CreateOrUpdateAsync(
                        WaitUntil.Completed,
                        gatewayName,
                        data);
                    NatGatewayResource result = lro.Value;
                    NatGatewayData resourceData = result.Data;

                    this.logger.LogInformation(
                        $"nat gateway creation succeeded. id: {resourceData.Id}");
                    return result;
                }
            }
        }

        async Task<ContainerServiceManagedClusterResource> CreateAksClusterAsync(
            string location,
            ResourceGroupResource resourceGroupResource,
            string clClusterName,
            VirtualNetworkResource vnet)
        {
            var aksClusterName = this.ToAksName(clClusterName);
            if (!forceCreate)
            {
                try
                {
                    var cluster = await resourceGroupResource.GetContainerServiceManagedClusterAsync(
                        aksClusterName);
                    this.logger.LogInformation(
                        $"Found existing aks cluster so skipping creation: {aksClusterName}");
                    return cluster;
                }
                catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
                {
                    // Does not exist. Proceed to creation.
                }
            }

            ContainerServiceManagedClusterCollection collection =
                resourceGroupResource.GetContainerServiceManagedClusters();
            this.logger.LogInformation(
                $"Starting aks cluster creation: {aksClusterName}");
            ContainerServiceManagedClusterData data = new(new AzureLocation(location))
            {
                Tags =
                {
                    {
                        CleanRoomClusterTag,
                        clClusterName
                    }
                },
                Sku = new ManagedClusterSku()
                {
                    Name = ManagedClusterSkuName.Base,
                    Tier = ManagedClusterSkuTier.Standard,
                },
                DnsPrefix = $"{aksClusterName}-dns",
                AgentPoolProfiles =
                {
                    new ManagedClusterAgentPoolProfile(AgentPoolName)
                    {
                        VnetSubnetId = new ResourceIdentifier(vnet.Id + $"/subnets/{AksSubnetName}"),
                        Count = 2,
                        VmSize = "Standard_D4ds_v5",
                        OSType = ContainerServiceOSType.Linux,
                        AgentPoolType = AgentPoolType.VirtualMachineScaleSets,
                        EnableNodePublicIP = false,
                        Mode = AgentPoolMode.System
                    }
                },
                NetworkProfile = new ContainerServiceNetworkProfile()
                {
                    NetworkPlugin = ContainerServiceNetworkPlugin.Azure,
                    NetworkPolicy = ContainerServiceNetworkPolicy.Calico,
                    NetworkDataplane = NetworkDataplane.Azure,
                    LoadBalancerSku = "Standard",
                    OutboundType = "loadBalancer",
                    LoadBalancerProfile = new ManagedClusterLoadBalancerProfile()
                    {
                        ManagedOutboundIPs =
                        new ManagedClusterLoadBalancerProfileManagedOutboundIPs()
                        {
                            Count = 1
                        }
                    },
                    ServiceCidr = "10.4.0.0/16",
                    DnsServiceIP = "10.4.0.10",
                    ServiceCidrs = { "10.4.0.0/16" },
                },
                StorageProfile = new ManagedClusterStorageProfile()
                {
                    IsBlobCsiDriverEnabled = true,
                    IsFileCsiDriverEnabled = true,
                },
                OidcIssuerProfile = new ManagedClusterOidcIssuerProfile()
                {
                    IsEnabled = true,
                },
                SecurityProfile = new ManagedClusterSecurityProfile()
                {
                    IsWorkloadIdentityEnabled = true
                },
                AutoUpgradeProfile = new ManagedClusterAutoUpgradeProfile()
                {
                    UpgradeChannel = UpgradeChannel.Patch,
                    NodeOSUpgradeChannel = ManagedClusterNodeOSUpgradeChannel.NodeImage,
                },
                ServicePrincipalProfile = new ManagedClusterServicePrincipalProfile("msi"),
                EnableRbac = true,
                Identity = new ManagedServiceIdentity("SystemAssigned")
            };

            ArmOperation<ContainerServiceManagedClusterResource> lro =
                await collection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                aksClusterName,
                data);
            ContainerServiceManagedClusterResource result = lro.Value;
            ContainerServiceManagedClusterData resourceData = result.Data;

            this.logger.LogInformation(
                $"Aks cluster creation succeeded. id: {resourceData.Id}");
            return result;
        }

        async Task UpdateAgentPoolMiPermissionsOnMCRgAsync(
            ContainerServiceManagedClusterResource aks)
        {
            var mcResourceGroup = aks.Data.NodeResourceGroup;
            var agentPoolMiName = aks.Data.Name + "-" + AgentPoolName;

            ResourceIdentifier mcResourceGroupResourceId =
                ResourceGroupResource.CreateResourceIdentifier(subscriptionId, mcResourceGroup);
            ResourceGroupResource mcResourceGroupResource =
                client.GetResourceGroupResource(mcResourceGroupResourceId);

            UserAssignedIdentityResource uid =
                await mcResourceGroupResource.GetUserAssignedIdentityAsync(agentPoolMiName);

            // TODO (gsinha): Avoid contributor role and use a more targeted role defn.
            string contributorRoleDefinitionId = $"/subscriptions/{subscriptionId}/providers/" +
                $"Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";
            string roleAssignmentId = Guid.NewGuid().ToString();

            var roleAssignmentData =
                new RoleAssignmentCreateOrUpdateContent(
                    new ResourceIdentifier(contributorRoleDefinitionId),
                    uid.Data.PrincipalId!.Value)
                {
                    PrincipalType = "ServicePrincipal",
                };

            var collection = mcResourceGroupResource.GetRoleAssignments();
            try
            {
                await collection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    roleAssignmentId,
                    roleAssignmentData);
            }
            catch (RequestFailedException rfe) when (rfe.ErrorCode == "RoleAssignmentExists")
            {
                // Already exists. Ignore failure.
            }

            this.logger.LogInformation(
                $"Contributor role assignment over rg {mcResourceGroup} succeeded.");
        }

        async Task UpdateAgentPoolMiPermissionsOnVNetRgAsync(
            ContainerServiceManagedClusterResource aks,
            VirtualNetworkResource vnet)
        {
            var mcResourceGroup = aks.Data.NodeResourceGroup;
            var agentPoolMiName = aks.Data.Name + "-" + AgentPoolName;

            ResourceIdentifier mcResourceGroupResourceId =
                ResourceGroupResource.CreateResourceIdentifier(subscriptionId, mcResourceGroup);
            ResourceGroupResource mcResourceGroupResource =
                client.GetResourceGroupResource(mcResourceGroupResourceId);

            UserAssignedIdentityResource uid =
                await mcResourceGroupResource.GetUserAssignedIdentityAsync(agentPoolMiName);

            // TODO (gsinha): Avoid contributor role and use a more targeted role defn.
            string contributorRoleDefinitionId = $"/subscriptions/{subscriptionId}/providers/" +
                $"Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";
            string roleAssignmentId = Guid.NewGuid().ToString();

            var roleAssignmentData =
                new RoleAssignmentCreateOrUpdateContent(
                    new ResourceIdentifier(contributorRoleDefinitionId),
                    uid.Data.PrincipalId!.Value)
                {
                    PrincipalType = "ServicePrincipal",
                };

            var vnetResourceGroup = vnet.Id.ResourceGroupName;
            ResourceIdentifier vnetResourceGroupResourceId =
                ResourceGroupResource.CreateResourceIdentifier(subscriptionId, vnetResourceGroup);
            ResourceGroupResource vnetResourceGroupResource =
                client.GetResourceGroupResource(vnetResourceGroupResourceId);
            var collection = vnetResourceGroupResource.GetRoleAssignments();
            try
            {
                await collection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    roleAssignmentId,
                    roleAssignmentData);
            }
            catch (RequestFailedException rfe) when (rfe.ErrorCode == "RoleAssignmentExists")
            {
                // Already exists. Ignore failure.
            }

            this.logger.LogInformation(
                $"Contributor role assignment over rg {vnetResourceGroup} succeeded.");
        }
    }

    public async Task<CleanRoomCluster?> UpdateCluster(
        string clClusterName,
        CleanRoomClusterInput input,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();

        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        var aks = await this.TryGetManagedCluster(clClusterName, resourceGroupResource);
        var vnet = await this.TryGetVirtualNetwork(clClusterName, resourceGroupResource);

        if (aks == null || vnet == null)
        {
            return null;
        }

        _ = bool.TryParse(providerConfig["forceCreate"]?.ToString(), out bool forceCreate);
        _ = bool.TryParse(providerConfig["noWaitOnReady"]?.ToString(), out bool noWaitOnReady);
        string kubeConfigFile = await GetKubeConfigFile(aks);

        if (input.ObservabilityProfile != null && input.ObservabilityProfile.Enabled)
        {
            await this.EnableClusterObservabilityAsync(
                client,
                aks,
                resourceGroupResource,
                vnet,
                kubeConfigFile,
                clClusterName,
                forceCreate,
                noWaitOnReady,
                progressReporter);
        }

        if (input.AnalyticsWorkloadProfile != null && input.AnalyticsWorkloadProfile.Enabled)
        {
            await this.EnableAnalyticsWorkloadAsync(
                client,
                clClusterName,
                resourceGroupResource,
                kubeConfigFile,
                aks,
                vnet,
                input.AnalyticsWorkloadProfile,
                forceCreate,
                noWaitOnReady,
                progressReporter);
        }

        progressReporter.Report("Cluster update completed.");
        this.logger.LogInformation($"Cluster update completed: {clClusterName}");
        return await this.GetCluster(clClusterName, providerConfig);
    }

    public async Task<CleanRoomCluster> GetCluster(
        string clClusterName,
        JsonObject? providerConfig)
    {
        return await this.TryGetCluster(clClusterName, providerConfig) ??
            throw new Exception($"No Cluster found for {clClusterName}.");
    }

    public async Task<CleanRoomCluster?> TryGetCluster(
        string clClusterName,
        JsonObject? providerConfig)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        var aks = await this.TryGetManagedCluster(clClusterName, resourceGroupResource);
        var vnet = await this.TryGetVirtualNetwork(clClusterName, resourceGroupResource);

        if (aks != null && vnet != null)
        {
            string kubeConfigFile = await GetKubeConfigFile(aks);
            (bool analyticsWorkloadEnabled, string? analyticsAgentEndpoint) =
                await this.TryGetAnalyticsAgentEndpoint(kubeConfigFile);
            (bool observabilityEnabled, string? observabilityEndpoint) =
                await this.TryGetObservabilityEndpoint(kubeConfigFile);
            return this.ToCleanRoomCluster(
                clClusterName,
                vnet,
                aks,
                analyticsWorkloadEnabled,
                analyticsAgentEndpoint,
                observabilityEnabled,
                observabilityEndpoint);
        }

        return null;
    }

    public async Task DeleteCluster(string clClusterName, JsonObject? providerConfig)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);
        List<Task> deleteTasks = new();

        var clustersToDelete = await this.GetManagedClusters(clClusterName, resourceGroupResource);
        this.logger.LogInformation(
            $"Found {clustersToDelete.Count} aks cluster(s) to delete.");
        foreach (var resource in clustersToDelete)
        {
            deleteTasks.Add(Task.Run(async () =>
            {
                this.logger.LogInformation($"Deleting aks cluster {resource.Id}");
                await resource.DeleteAsync(WaitUntil.Completed);
            }));
        }

        await Task.WhenAll(deleteTasks);

        deleteTasks.Clear();
        var zonesToDelete = await this.GetPrivateDnsZones(clClusterName, resourceGroupResource);
        this.logger.LogInformation(
            $"Found {zonesToDelete.Count} private dns zone(s) to delete.");
        foreach (var resource in zonesToDelete)
        {
            deleteTasks.Add(Task.Run(async () =>
            {
                this.logger.LogInformation($"Deleting private dns zone {resource.Id}");
                await resource.DeleteAsync(WaitUntil.Completed);
            }));
        }

        await Task.WhenAll(deleteTasks);

        deleteTasks.Clear();
        var networksToDelete = await this.GetVirtualNetworks(clClusterName, resourceGroupResource);
        this.logger.LogInformation(
            $"Found {networksToDelete.Count} virtual network(s) to delete.");
        foreach (var resource in networksToDelete)
        {
            deleteTasks.Add(Task.Run(async () =>
            {
                this.logger.LogInformation($"Deleting virtual network zone {resource.Id}");
                await resource.DeleteAsync(WaitUntil.Completed);
            }));
        }

        await Task.WhenAll(deleteTasks);

        deleteTasks.Clear();
        var misToDelete = await this.GetManagedIdentities(clClusterName, resourceGroupResource);
        this.logger.LogInformation(
            $"Found {misToDelete.Count} managed identity(s) to delete.");
        foreach (var resource in misToDelete)
        {
            deleteTasks.Add(Task.Run(async () =>
            {
                this.logger.LogInformation($"Deleting managed identity {resource.Id}");
                await resource.DeleteAsync(WaitUntil.Completed);
            }));
        }

        await Task.WhenAll(deleteTasks);
    }

    public async Task<CleanRoomClusterKubeConfig?> TryGetClusterKubeConfig(
        string clClusterName,
        JsonObject? providerConfig)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);
        var aks = await this.TryGetManagedCluster(clClusterName, resourceGroupResource);
        if (aks == null)
        {
            return null;
        }

        var creds = await aks.GetClusterUserCredentialsAsync();
        return new CleanRoomClusterKubeConfig
        {
            Kubeconfig = creds.Value.Kubeconfigs[0].Value
        };
    }

    public async Task<AnalyticsWorkloadGeneratedDeployment> GenerateAnalyticsWorkloadDeployment(
        GenerateAnalyticsWorkloadDeploymentInput input,
        JsonObject? providerConfig)
    {
        var policyOption = SecurityPolicyConfigInput.Convert(input.SecurityPolicy);
        string agentPolicyRego;
        var telemetryCollectionEnabled = input.TelemetryProfile != null &&
            input.TelemetryProfile.CollectionEnabled;
        AgentChartValues agentChartValues;
        var contractData = await this.httpClientManager.GetContractData(
            this.logger,
            input.ContractUrl,
            input.ContractUrlCaCert);
        if (policyOption.PolicyCreationOption == SecurityPolicyCreationOption.allowAll)
        {
            var frontendSecurityPolicyDigest = AciConstants.AllowAllPolicyDigest;
            agentChartValues = AgentChartValues.ToAgentChartValues(
                contractData,
                telemetryCollectionEnabled,
                Constants.SparkFrontendEndpoint,
                frontendSecurityPolicyDigest);
            agentPolicyRego = AciConstants.AllowAllPolicyRego;
        }
        else
        {
            (var frontendPolicyRego, _) = await this.DownloadAndExpandSparkFrontendPolicy(
                policyOption.PolicyCreationOption,
                telemetryCollectionEnabled);
            var frontendSecurityPolicyDigest = this.ToPolicyDigest(frontendPolicyRego);
            agentChartValues = AgentChartValues.ToAgentChartValues(
                contractData,
                telemetryCollectionEnabled,
                Constants.SparkFrontendEndpoint,
                frontendSecurityPolicyDigest);
            (agentPolicyRego, _) = await this.DownloadAndExpandAnalyticsAgentPolicy(
                policyOption.PolicyCreationOption,
                agentChartValues);
        }

        var agentSecurityPolicyDigest = this.ToPolicyDigest(agentPolicyRego);

        return new AnalyticsWorkloadGeneratedDeployment
        {
            SecurityPolicyCreationOption = policyOption.PolicyCreationOption.ToString(),
            DeploymentTemplate = new()
            {
                ChartMetadata = new()
                {
                    Chart = ImageUtils.GetAnalyticsAgentChartPath(),
                    Version = ImageUtils.GetAnalyticsAgentChartVersion(),
                    Release = Constants.AnalyticsAgentReleaseName,
                    Namespace = Constants.AnalyticsAgentNamespace
                },
                Values = agentChartValues
            },
            GovernancePolicy = new()
            {
                Type = "add",
                Claims = new()
                {
                    IsDebuggable = false,
                    HostData = agentSecurityPolicyDigest
                }
            },
            CcePolicy = new()
            {
                Value = this.ToPolicyBase64(agentPolicyRego),
                DocumentUrl = ImageUtils.GetAnalyticsAgentSecurityPolicyDocumentUrl()
            }
        };
    }

    private static ODataError? ValidateInput(JsonObject? providerConfig)
    {
        if (providerConfig == null)
        {
            return new ODataError(
                code: "ProviderConfigMissing",
                message: "Provider configuraton missing.");
        }

        if (string.IsNullOrEmpty(providerConfig!["location"]?.ToString()))
        {
            return new ODataError(
                code: "LocationgMissing",
                message: "Location input must be provided.");
        }

        if (string.IsNullOrEmpty(providerConfig!["subscriptionId"]?.ToString()))
        {
            return new ODataError(
                code: "SubscriptionIdMissing",
                message: "SubscriptionId input must be provided.");
        }

        if (string.IsNullOrEmpty(providerConfig!["resourceGroupName"]?.ToString()))
        {
            return new ODataError(
                code: "ResourceGroupNameMissing",
                message: "ResourceGroupName input must be provided.");
        }

        if (string.IsNullOrEmpty(providerConfig!["tenantId"]?.ToString()))
        {
            return new ODataError(
                code: "TenantIdMissing",
                message: "TenantId input must be provided.");
        }

        return null;
    }

    private static async Task<string> GetKubeConfigFile(ContainerServiceManagedClusterResource aks)
    {
        var creds = await aks.GetClusterUserCredentialsAsync();
        string outDir = Path.GetTempPath();
        var kubeConfigFile = Path.Combine(outDir, $"{aks.Data.Name}.config");
        File.WriteAllBytes(kubeConfigFile, creds.Value.Kubeconfigs[0].Value);
        return kubeConfigFile;
    }

    private async Task CreateNamespaceAsync(string ns, string kubeConfigFile)
    {
        this.logger.LogInformation($"Creating namespace {ns}.");
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        await kubectlClient.CreateNamespaceAsync(ns);
        this.logger.LogInformation($"Namespace created.");
    }

    private async Task CreateSparkOperatorServiceAccountRbac(string ns, string kubeConfigFile)
    {
        this.logger.LogInformation($"Setting up spark operator service account rbac for {ns}.");
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        await kubectlClient.CreateSparkOperatorServiceAccountRbac(ns);
        this.logger.LogInformation($"Spark operator service account rbac setup complete.");
    }

    private async Task InstallVN2OnAksAsync(
        ContainerServiceManagedClusterResource aks,
        string kubeConfigFile)
    {
        this.logger.LogInformation(
            $"Starting installation of VN2 helm chart on: {aks.Data.Name}");
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallVN2Chart("vn2");
        this.logger.LogInformation($"VN2 helm chart installation succeeded.");
    }

    private async Task InstallSparkOperatorOnAksAsync(
        ContainerServiceManagedClusterResource aks,
        string kubeConfigFile)
    {
        this.logger.LogInformation(
            $"Starting installation of Spark-Operator helm chart on: {aks.Data.Name}");
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallSparkOperatorChart("spark-operator");
        this.logger.LogInformation($"Spark-Operator helm chart installation succeeded.");
    }

    private async Task WaitForSparkOperatorUp(string kubeConfigFile)
    {
        this.logger.LogInformation($"Waiting for Spark-Operator pod/deployment to become ready.");
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        await kubectlClient.WaitForSparkOperatorUp("spark-operator");
        this.logger.LogInformation($"Spark-Operator pod/deployment are reporting ready.");
    }

    private async Task InstallAnalyticsAgentOnAksAsync(
        ContainerServiceManagedClusterResource aks,
        AnalyticsWorkloadProfileInput input,
        PublicIPAddressResource publicIP,
        string dnsLabel,
        string kubeConfigFile)
    {
        string ccrFqdn = $"{dnsLabel}.{publicIP.Data.Location}.cloudapp.azure.com";
        var deploymentTemplate = await this.httpClientManager.GetDeploymentTemplate(
            this.logger,
            input.ConfigurationUrl!,
            input.ConfigurationUrlCaCert);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        var valuesOverrideFiles = await GenerateValuesOverrideFiles();

        this.logger.LogInformation(
            $"Starting installation of cleanroom-spark-analytics-agent helm chart on: " +
            $"{aks.Data.Name}");
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallAnalyticsAgentChart(
            Constants.AnalyticsAgentReleaseName,
            Constants.AnalyticsAgentNamespace,
            valuesOverrideFiles,
            serviceAnnotations: new()
            {
                {
                    "service.beta.kubernetes.io/azure-load-balancer-resource-group",
                    aks.Data.NodeResourceGroup
                },
                {
                    "service.beta.kubernetes.io/azure-pip-name",
                    publicIP.Data.Name
                },
                {
                    "service.beta.kubernetes.io/azure-dns-label-name",
                    dnsLabel
                },
                {
                    Constants.ServiceFqdnAnnotation,
                    ccrFqdn
                }
            });
        this.logger.LogInformation($"Cleanroom-spark-analytics-agent helm chart installation " +
            $"succeeded.");

        async Task<List<string>> GenerateValuesOverrideFiles()
        {
            var securityPolicy = await GetContainerGroupSecurityPolicy();

            List<string> files = new();
            var values = deploymentTemplate.Values;
            var app = await File.ReadAllTextAsync(
                "spark-analytics-agent/values.app.yaml");
            string caType = !string.IsNullOrEmpty(values.CcrgovEndpoint) ? "cgs" : "local";
            var discovery = values.CcrgovServiceCertDiscovery ?? new ServiceCertDiscoveryInput();
            app = app.Replace("<CA_TYPE>", caType);
            app = app.Replace("<CCR_FQDN>", ccrFqdn);
            app = app.Replace("<CCR_GOV_ENDPOINT>", values.CcrgovEndpoint);
            app = app.Replace("<CCR_GOV_API_PATH_PREFIX>", values.CcrgovApiPathPrefix);
            app = app.Replace("<CCR_GOV_SERVICE_CERT>", values.CcrgovServiceCert);
            app = app.Replace("<CCR_GOV_SERVICE_CERT_DISCOVERY_ENDPOINT>", discovery.Endpoint);
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_SNP_HOST_DATA>",
                discovery.SnpHostData);
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_SKIP_DIGEST_CHECK>",
                discovery.SkipDigestCheck.ToString().ToLower());
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_CONSTITUTION_DIGEST>",
                discovery.ConstitutionDigest);
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_JSAPP_BUNDLE_DIGEST>",
                discovery.JsappBundleDigest);
            app = app.Replace("<SPARK_FRONTEND_ENDPOINT>", values.SparkFrontendEndpoint);
            app = app.Replace("<SPARK_FRONTEND_SNP_HOST_DATA>", values.SparkFrontendSnpHostData);
            app = app.Replace("<CCF_NETWORK_RECOVERY_MEMBERS>", values.CcfNetworkRecoveryMembers);
            app = app.Replace(
                "<ANALYTICS_AGENT_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.AnalyticsAgent]);
            app = app.Replace(
                "<CCR_PROXY_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.CcrProxy]);
            app = app.Replace(
                "<CCR_SKR_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.Skr]);
            app = app.Replace(
                "<CCR_ATTESTATION_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.CcrAttestation]);
            app = app.Replace(
                "<OTEL_COLLECTOR_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.OtelCollector]);
            app = app.Replace(
                "<CCR_GOVERNANCE_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.CcrGovernance]);

            var telemetryReplacements = new Dictionary<string, string>();
            var telemetryCollectionEnabled = input.TelemetryProfile != null &&
                input.TelemetryProfile.CollectionEnabled;
            if (telemetryCollectionEnabled)
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "true";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] =
                    $"{Constants.PrometheusServiceEndpoint}:9090/api/v1/write";
                telemetryReplacements["<LOKI_ENDPOINT>"] =
                    $"{Constants.LokiServiceEndpoint}:3100/otlp";
                telemetryReplacements["<TEMPO_ENDPOINT>"] =
                    $"{Constants.TempoServiceEndpoint}:4317";
            }
            else
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "false";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<LOKI_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<TEMPO_ENDPOINT>"] = string.Empty;
            }

            foreach (var kvp in telemetryReplacements)
            {
                app = app.Replace(kvp.Key, kvp.Value);
            }

            var valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, app);
            files.Add(valuesOverridesFile);

            var caci = await File.ReadAllTextAsync(
                "spark-analytics-agent/values.caci.yaml");
            caci = caci.Replace("<CCE_POLICY>", securityPolicy.ConfidentialComputeCcePolicy);
            valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, caci);
            files.Add(valuesOverridesFile);
            return files;
        }

        async Task<ContainerGroupSecurityPolicy> GetContainerGroupSecurityPolicy()
        {
            var policyOption = SecurityPolicyConfigInput.Convert(input.SecurityPolicy);
            this.logger.LogInformation($"policyCreationOption: {policyOption.PolicyCreationOption}");
            if (policyOption.PolicyCreationOption == SecurityPolicyCreationOption.allowAll ||
                policyOption.PolicyCreationOption == SecurityPolicyCreationOption.userSupplied)
            {
                var ccePolicyInput = policyOption.PolicyCreationOption ==
                    SecurityPolicyCreationOption.allowAll ?
                    AciConstants.AllowAllPolicyRegoBase64 : policyOption.Policy!;
                return new ContainerGroupSecurityPolicy
                {
                    ConfidentialComputeCcePolicy = ccePolicyInput,
                    Images = new()
                {
                    {
                        AciConstants.ContainerName.AnalyticsAgent,
                        $"{ImageUtils.AnalyticsAgentImage()}:" +
                        $"{ImageUtils.AnalyticsAgentTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrAttestation,
                        $"{ImageUtils.CcrAttestationImage()}:{ImageUtils.CcrAttestationTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrGovernance,
                        $"{ImageUtils.CcrGovernanceImage()}:{ImageUtils.CcrGovernanceTag()}"
                    },
                    {
                        AciConstants.ContainerName.Skr,
                        $"{ImageUtils.SkrImage()}:{ImageUtils.SkrTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrProxy,
                        $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}"
                    },
                    {
                        AciConstants.ContainerName.OtelCollector,
                        $"{ImageUtils.OtelCollectorImage()}:{ImageUtils.OtelCollectorTag()}"
                    },
                }
                };
            }

            (var policyRego, var policyDocument) = await this.DownloadAndExpandAnalyticsAgentPolicy(
                policyOption.PolicyCreationOption,
                deploymentTemplate.Values);

            var ccePolicy = this.ToPolicyBase64(policyRego);

            var policyContainers = policyDocument.Containers.ToDictionary(x => x.Name, x => x);
            List<string> requiredContainers =
                [
                    AciConstants.ContainerName.AnalyticsAgent,
                    AciConstants.ContainerName.CcrAttestation,
                    AciConstants.ContainerName.CcrGovernance,
                    AciConstants.ContainerName.Skr,
                    AciConstants.ContainerName.CcrProxy,
                    AciConstants.ContainerName.OtelCollector,
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
    }

    private async Task EnableClusterObservabilityAsync(
        ArmClient client,
        ContainerServiceManagedClusterResource aks,
        ResourceGroupResource resourceGroup,
        VirtualNetworkResource vnet,
        string kubeConfigFile,
        string clClusterName,
        bool forceCreate,
        bool noWaitOnReady,
        IProgress<string> progressReporter)
    {
        string aksClusterName = this.ToAksName(clClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);

        this.logger.LogInformation("Updating AKS MI permissions on the VNet RG for " +
            "observability collection...");

        // Update AKS MI to have permissions to mount storage as NFS in the vnet's RG.
        await UpdateAksMiPermissionsOnVnetRgAsync(client, aks, vnet);

        this.logger.LogInformation($"Creating namespace {Constants.ObservabilityNamespace}.");
        await kubectlClient.CreateNamespaceAsync(Constants.ObservabilityNamespace);

        progressReporter.Report("Creating private dns zone for observability...");
        var privateDnsResource = await this.CreatePrivateDNSZoneAsync(
            resourceGroup,
            clClusterName,
            Constants.ObservabilityZoneName,
            forceCreate);

        progressReporter.Report("Creating private dns link for observability...");
        await this.CreatePrivateDnsLinkAsync(
            clClusterName,
            privateDnsResource,
            vnet,
            forceCreate);

        this.logger.LogInformation($"Namespace created.");
        progressReporter.Report("Installing prometheus, loki, tempo and grafana...");

        var prometheusTask = helmClient.InstallPrometheusChart(
            Constants.PrometheusReleaseName,
            Constants.ObservabilityNamespace,
            ["observability/prometheus/values.caci.yaml"]);
        var lokiTask = helmClient.InstallLokiChart(
            Constants.LokiReleaseName,
            Constants.ObservabilityNamespace,
            ["observability/loki/values.caci.yaml"]);
        var tempoTask = helmClient.InstallTempoChart(
            Constants.TempoReleaseName,
            Constants.ObservabilityNamespace,
            ["observability/tempo/values.caci.yaml"]);
        var grafanaDashboardsTask = kubectlClient.InstallGrafanaDashboards(
            Constants.ObservabilityNamespace);
        var grafanaChartTask = helmClient.InstallGrafanaChart(
            Constants.GrafanaReleaseName,
            Constants.ObservabilityNamespace,
            ["observability/grafana/values.caci.yaml"]);

        await Task.WhenAll(
            prometheusTask,
            lokiTask,
            tempoTask,
            grafanaDashboardsTask,
            grafanaChartTask);

        this.logger.LogInformation($"Prometheus, Loki, Tempo, and Grafana installations succeeded.");

        if (noWaitOnReady)
        {
            this.logger.LogInformation(
                $"Skipping wait for prometheus, loki, tempo and grafana to become ready " +
                $"on: {aksClusterName}");
            return;
        }

        this.logger.LogInformation(
            $"Waiting for prometheus to become ready on: {aksClusterName}");
        progressReporter.Report("Waiting for prometheus to become ready...");
        await kubectlClient.WaitForPrometheusUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Prometheus is ready on: {aksClusterName}");

        this.logger.LogInformation(
            $"Waiting for loki to become ready on: {aksClusterName}");
        progressReporter.Report("Waiting for loki to become ready...");
        await kubectlClient.WaitForLokiUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Loki is ready on: {aksClusterName}");

        this.logger.LogInformation(
            $"Waiting for tempo to become ready on: {aksClusterName}");
        progressReporter.Report("Waiting for tempo to become ready...");
        await kubectlClient.WaitForTempoUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Tempo is ready on: {aksClusterName}");

        this.logger.LogInformation(
            $"Waiting for grafana to become ready on: {aksClusterName}");
        progressReporter.Report("Waiting for grafana to become ready...");
        await kubectlClient.WaitForGrafanaUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Grafana is ready on: {aksClusterName}");

        async Task UpdateAksMiPermissionsOnVnetRgAsync(
            ArmClient client,
            ContainerServiceManagedClusterResource aks,
            VirtualNetworkResource vnet)
        {
            var subscriptionId = vnet.Id.SubscriptionId;
            Guid clusterMi = aks.Data.ClusterIdentity.PrincipalId!.Value;

            // This is required for the blob CSI driver to create storage accounts and
            // enable NFS.
            string contributorRoleDefinitionId = $"/subscriptions/{subscriptionId}/providers/" +
                $"Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c";
            string roleAssignmentId = Guid.NewGuid().ToString();

            var roleAssignmentData =
                new RoleAssignmentCreateOrUpdateContent(
                    new ResourceIdentifier(contributorRoleDefinitionId),
                    clusterMi)
                {
                    PrincipalType = "ServicePrincipal",
                };

            var vnetResourceGroup = vnet.Id.ResourceGroupName;

            ResourceIdentifier vnetResourceGroupResourceId =
                ResourceGroupResource.CreateResourceIdentifier(subscriptionId, vnetResourceGroup);
            ResourceGroupResource vnetResourceGroupResource =
                client.GetResourceGroupResource(vnetResourceGroupResourceId);
            var collection = vnetResourceGroupResource.GetRoleAssignments();
            try
            {
                await collection.CreateOrUpdateAsync(
                    WaitUntil.Completed,
                    roleAssignmentId,
                    roleAssignmentData);
            }
            catch (RequestFailedException rfe) when (rfe.ErrorCode == "RoleAssignmentExists")
            {
                // Already exists. Ignore failure.
            }

            this.logger.LogInformation(
                $"Contributor role assignment over vnet {vnet.Data.Name} succeeded.");
        }
    }

    private async Task WaitForAnalyticsAgentUp(string kubeConfigFile)
    {
        this.logger.LogInformation($"Waiting for analytics agent pod/deployment to become ready.");
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        await kubectlClient.WaitForAnalyticsAgentUp(Constants.AnalyticsAgentNamespace);
        this.logger.LogInformation($"Analytics agent pod/deployment are reporting ready.");
    }

    private async Task WaitForWorkloadIdentityDeploymentUp(string kubeConfigFile)
    {
        this.logger.LogInformation($"Waiting for workload identity deployment to become ready.");
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        await kubectlClient.WaitForWorkloadIdentityDeploymentUp();
        this.logger.LogInformation($"Workload identity deployment is reporting ready.");
    }

    private async Task<(bool found, string? endpoint)> TryGetAnalyticsAgentEndpoint(
        string kubeConfigFile)
    {
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        return await kubectlClient.TryGetAnalyticsAgentEndpoint(Constants.AnalyticsAgentNamespace);
    }

    private async Task<(bool found, string? endpoint)> TryGetObservabilityEndpoint(
        string kubeConfigFile)
    {
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        return await kubectlClient.TryGetObservabilityEndpoint(Constants.ObservabilityNamespace);
    }

    private async Task<UserAssignedIdentityResource> InstallExternalDnsOnAksAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        ContainerServiceManagedClusterResource aks,
        JsonObject providerConfig,
        string kubeConfigFile)
    {
        string ns = "external-dns";
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        var workloadMi = await SetupWorkloadIdentityForExternalDnsAsync();
        await CreateExternalDnsNamespace();
        await CreateExternalDnsAzureConfigSecret();

        this.logger.LogInformation(
            $"Starting installation of external-dns helm chart on: {aks.Data.Name}");
        var wiClientId = workloadMi.Data.ClientId!.Value.ToString();
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallExternalDnsChart("external-dns", ns, wiClientId);
        this.logger.LogInformation($"External-dns helm chart installation succeeded.");
        return workloadMi;

        async Task<UserAssignedIdentityResource> SetupWorkloadIdentityForExternalDnsAsync()
        {
            var mi = await CreateWorkloadManagedIdentity();
            await SetupExternalDnsFederatedCredentials(mi, aks, ns, "external-dns");
            return mi;

            async Task<UserAssignedIdentityResource> CreateWorkloadManagedIdentity()
            {
                string miName = ExternalDnsWorkloadMiName;
                bool.TryParse(providerConfig["forceCreate"]?.ToString(), out bool forceCreate);
                UserAssignedIdentityResource? mi = null;
                if (!forceCreate)
                {
                    try
                    {
                        mi = await resourceGroupResource.GetUserAssignedIdentityAsync(miName);
                        this.logger.LogInformation(
                            $"Found existing mi so skipping creation: {miName}");
                    }
                    catch (RequestFailedException rfe)
                    when (rfe.Status == (int)HttpStatusCode.NotFound)
                    {
                        // Does not exist. Proceed to creation.
                    }
                }

                if (mi == null)
                {
                    this.logger.LogInformation(
                        $"Starting creation of managed identity for workload identity.");
                    UserAssignedIdentityData data = new(aks.Data.Location)
                    {
                        Tags =
                        {
                            {
                                CleanRoomClusterTag,
                                clClusterName
                            }
                        },
                    };
                    var collection = resourceGroupResource.GetUserAssignedIdentities();
                    mi = (await collection.CreateOrUpdateAsync(
                        WaitUntil.Completed,
                        miName,
                        data)).Value;

                    this.logger.LogInformation(
                        $"Managed identity creation for workload identity succeeded. id:" +
                        $" {mi.Data.Id}");
                }

                return mi;
            }

            async Task SetupExternalDnsFederatedCredentials(
                UserAssignedIdentityResource mi,
                ContainerServiceManagedClusterResource aks,
                string serviceAccountNamespace,
                string serviceAccountName)
            {
                var fcName = "external-dns-fc";
                bool.TryParse(providerConfig["forceCreate"]?.ToString(), out bool forceCreate);
                FederatedIdentityCredentialResource? fc = null;
                if (!forceCreate)
                {
                    try
                    {
                        fc = await mi.GetFederatedIdentityCredentialAsync(fcName);
                    }
                    catch (RequestFailedException rfe) when
                    (rfe.Status == (int)HttpStatusCode.NotFound)
                    {
                        // Does not exist. Proceed to creation.
                    }
                }

                var subject =
                    $"system:serviceaccount:{serviceAccountNamespace}:{serviceAccountName}";
                var issuerUri = new Uri(aks.Data.OidcIssuerProfile.IssuerUriInfo);
                if (fc == null || fc.Data.Subject != subject || fc.Data.IssuerUri != issuerUri)
                {
                    this.logger.LogInformation(
                        $"Starting creation of federated credential for workload identity.");
                    FederatedIdentityCredentialData data = new()
                    {
                        IssuerUri = issuerUri,
                        Subject = subject,
                        Audiences = { "api://AzureADTokenExchange" },
                    };

                    var collection = mi.GetFederatedIdentityCredentials();
                    fc = (await collection.CreateOrUpdateAsync(
                        WaitUntil.Completed,
                        fcName,
                        data)).Value;

                    this.logger.LogInformation(
                        $"Federated credential creation for workload identity succeeded. id:" +
                        $" {fc.Data.Id}");
                }
                else
                {
                    this.logger.LogInformation(
                        $"Found existing federated credential so skipping creation: {fcName}");
                }
            }
        }

        async Task CreateExternalDnsNamespace()
        {
            this.logger.LogInformation($"Creating K8s namespace: {ns}");
            var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
            await kubectlClient.CreateExternalDnsNamespace(ns);
            this.logger.LogInformation($"K8s namespace creation succeeded.");
        }

        async Task CreateExternalDnsAzureConfigSecret()
        {
            string tenantId = providerConfig!["tenantId"]!.ToString();
            this.logger.LogInformation(
                $"Creating K8s secret with azure dns configuration: {aks.Data.Name}");
            var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
            await kubectlClient.CreateExternalDnsAzureConfigSecret(
                ns,
                tenantId,
                subscriptionId,
                resourceGroupName);

            this.logger.LogInformation($"K8s secret creation succeeded.");
        }
    }

    private async Task UpdateMiPermissionsForPrivateDnsZoneAsync(
        ResourceGroupResource resourceGroupResource,
        UserAssignedIdentityResource mi)
    {
        string privateDnsZoneContributorRoleDefinitionId =
            $"/subscriptions/{resourceGroupResource.Id.SubscriptionId}/providers/" +
            $"Microsoft.Authorization/roleDefinitions/b12aa53e-6015-4669-85d0-8515ebb3ae7f";
        string roleAssignmentId = Guid.NewGuid().ToString();

        var roleAssignmentData =
            new RoleAssignmentCreateOrUpdateContent(
                new ResourceIdentifier(privateDnsZoneContributorRoleDefinitionId),
                mi.Data.PrincipalId!.Value)
            {
                PrincipalType = "ServicePrincipal",
            };

        var collection = resourceGroupResource.GetRoleAssignments();
        try
        {
            await collection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                roleAssignmentId,
                roleAssignmentData);
        }
        catch (RequestFailedException rfe) when (rfe.ErrorCode == "RoleAssignmentExists")
        {
            // Already exists. Ignore failure.
        }

        this.logger.LogInformation(
            $"Private Dns Zone contributor role assignment over " +
            $"{resourceGroupResource.Id.Name} succeeded.");

        string readerRoleDefinitionId =
            $"/subscriptions/{resourceGroupResource.Id.SubscriptionId}/providers/" +
            $"Microsoft.Authorization/roleDefinitions/acdd72a7-3385-48ef-bd42-f606fba81ae7";
        roleAssignmentId = Guid.NewGuid().ToString();

        roleAssignmentData =
            new RoleAssignmentCreateOrUpdateContent(
                new ResourceIdentifier(readerRoleDefinitionId),
                mi.Data.PrincipalId!.Value)
            {
                PrincipalType = "ServicePrincipal",
            };

        collection = resourceGroupResource.GetRoleAssignments();
        try
        {
            await collection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                roleAssignmentId,
                roleAssignmentData);
        }
        catch (RequestFailedException rfe) when (rfe.ErrorCode == "RoleAssignmentExists")
        {
            // Already exists. Ignore failure.
        }

        this.logger.LogInformation(
            $"Reader role assignment over {resourceGroupResource.Id.Name} succeeded.");
    }

    private async Task WaitForExternalDnsUp(string kubeConfigFile)
    {
        this.logger.LogInformation($"Waiting for external-dns pod/deployment to become ready.");
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        await kubectlClient.WaitForExternalDnsUp("external-dns");
        this.logger.LogInformation($"External-dns pod/deployment are reporting ready.");
    }

    private async Task InstallSparkFrontendOnAksAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        ContainerServiceManagedClusterResource aks,
        VirtualNetworkResource vnet,
        AnalyticsWorkloadProfileInput input,
        string kubeConfigFile,
        bool forceCreate)
    {
        // Create a private DNS zone for the frontend service namespace so that the frontend svc
        // fqdn resolves successfully when the analytics agent connects via it.
        var privateZone = await this.CreatePrivateDNSZoneAsync(
            resourceGroupResource,
            clClusterName,
            Constants.SparkFrontendServiceZoneName,
            forceCreate);
        await this.CreatePrivateDnsLinkAsync(
            clClusterName,
            privateZone,
            vnet,
            forceCreate);
        var telemetryCollectionEnabled = input.TelemetryProfile != null &&
            input.TelemetryProfile.CollectionEnabled;
        var valuesOverrideFiles = await GenerateValuesOverrideFiles();

        this.logger.LogInformation(
            $"Starting installation of Spark Frontend helm chart on: {aks.Data.Name}");
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallSparkFrontendChart(
            Constants.SparkFrontendReleaseName,
            Constants.SparkFrontendServiceNamespace,
            valuesOverrideFiles);
        this.logger.LogInformation($"Spark Frontend helm chart installation succeeded.");

        async Task<List<string>> GenerateValuesOverrideFiles()
        {
            var policyOption = SecurityPolicyConfigInput.Convert(input.SecurityPolicy);
            var securityPolicy = await GetContainerGroupSecurityPolicy();

            List<string> files = new();
            var app = await File.ReadAllTextAsync(
                "spark-frontend/values.app.yaml");
            app = app.Replace(
                "<SPARK_FRONTEND_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.SparkFrontend]);
            app = app.Replace(
                "<CCR_PROXY_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.CcrProxy]);
            app = app.Replace(
                "<CCR_ATTESTATION_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.CcrAttestation]);
            app = app.Replace(
                "<OTEL_COLLECTOR_IMAGE_URL>",
                securityPolicy.Images[AciConstants.ContainerName.OtelCollector]);
            app = app.Replace("<CLEANROOM_REGISTRY_URL>", $"{ImageUtils.RegistryUrl()}");
            app = app.Replace(
                "<CLEANROOM_VERSIONS_DOCUMENT>",
                $"{ImageUtils.GetCleanroomVersionsDocumentUrl()}");
            app = app.Replace(
                "<CLEANROOM_ANALYTICS_IMAGE_URL>",
                $"{ImageUtils.CleanroomAnalyticsApp()}");
            app = app.Replace(
                "<CLEANROOM_ANALYTICS_IMAGE_POLICY_DOCUMENT_URL>",
                $"{ImageUtils.CleanroomAnalyticsAppPolicyDocument()}");
            app = app.Replace("<ANALYTICS_NAMESPACE>", Constants.AnalyticsWorkloadNamespace);
            app = app.Replace(
                "<ALLOW_ALL>",
                policyOption.PolicyCreationOption ==
                SecurityPolicyCreationOption.allowAll ? "true" : "false");
            app = app.Replace(
                "<DEBUG_MODE>",
                policyOption.PolicyCreationOption ==
                SecurityPolicyCreationOption.cachedDebug ? "true" : "false");
            app = app.Replace(
                "<CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL>",
                $"{ImageUtils.SidecarsPolicyDocumentRegistryUrl()}");
            var telemetryReplacements = new Dictionary<string, string>();
            if (telemetryCollectionEnabled)
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "true";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] =
                    $"{Constants.PrometheusServiceEndpoint}:9090/api/v1/write";
                telemetryReplacements["<LOKI_ENDPOINT>"] =
                    $"{Constants.LokiServiceEndpoint}:3100/otlp";
                telemetryReplacements["<TEMPO_ENDPOINT>"] =
                    $"{Constants.TempoServiceEndpoint}:4317";
            }
            else
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "false";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<LOKI_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<TEMPO_ENDPOINT>"] = string.Empty;
            }

            foreach (var kvp in telemetryReplacements)
            {
                app = app.Replace(kvp.Key, kvp.Value);
            }

            var valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, app);
            files.Add(valuesOverridesFile);

            var caci = await File.ReadAllTextAsync(
                "spark-frontend/values.caci.yaml");
            caci = caci.Replace("<CCE_POLICY>", securityPolicy.ConfidentialComputeCcePolicy);
            valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, caci);
            files.Add(valuesOverridesFile);
            return files;
        }

        async Task<ContainerGroupSecurityPolicy> GetContainerGroupSecurityPolicy()
        {
            var policyOption = SecurityPolicyConfigInput.Convert(input.SecurityPolicy);
            this.logger.LogInformation($"policyCreationOption: {policyOption.PolicyCreationOption}");
            if (policyOption.PolicyCreationOption == SecurityPolicyCreationOption.allowAll ||
                policyOption.PolicyCreationOption == SecurityPolicyCreationOption.userSupplied)
            {
                var ccePolicyInput = policyOption.PolicyCreationOption ==
                    SecurityPolicyCreationOption.allowAll ?
                    AciConstants.AllowAllPolicyRegoBase64 : policyOption.Policy!;
                return new ContainerGroupSecurityPolicy
                {
                    ConfidentialComputeCcePolicy = ccePolicyInput,
                    Images = new()
                {
                    {
                        AciConstants.ContainerName.SparkFrontend,
                        $"{ImageUtils.SparkFrontendImage()}:" +
                        $"{ImageUtils.SparkFrontendTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrAttestation,
                        $"{ImageUtils.CcrAttestationImage()}:{ImageUtils.CcrAttestationTag()}"
                    },
                    {
                        AciConstants.ContainerName.CcrProxy,
                        $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}"
                    },
                    {
                        AciConstants.ContainerName.OtelCollector,
                        $"{ImageUtils.OtelCollectorImage()}:{ImageUtils.OtelCollectorTag()}"
                    },
                }
                };
            }

            (var policyRego, var policyDocument) = await this.DownloadAndExpandSparkFrontendPolicy(
                policyOption.PolicyCreationOption,
                telemetryCollectionEnabled);

            var ccePolicy = this.ToPolicyBase64(policyRego);

            var policyContainers = policyDocument.Containers.ToDictionary(x => x.Name, x => x);
            List<string> requiredContainers =
                [
                    AciConstants.ContainerName.SparkFrontend,
                    AciConstants.ContainerName.CcrAttestation,
                    AciConstants.ContainerName.CcrProxy,
                    AciConstants.ContainerName.OtelCollector
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
    }

    private async Task WaitForSparkFrontendUp(string kubeConfigFile)
    {
        this.logger.LogInformation($"Waiting for spark-frontend pod/deployment to become ready.");
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        await kubectlClient.WaitForSparkFrontendUp(Constants.SparkFrontendServiceNamespace);
        this.logger.LogInformation($"Spark-frontend pod/deployment are reporting ready.");
    }

    private async Task<ContainerServiceManagedClusterResource?> TryGetManagedCluster(
        string clClusterName,
        ResourceGroupResource resourceGroupResource)
    {
        var aksName = this.ToAksName(clClusterName);

        try
        {
            ContainerServiceManagedClusterResource clusterResource =
                await resourceGroupResource.GetContainerServiceManagedClusterAsync(aksName);
            return clusterResource;
        }
        catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
        {
            // Does not exist.
            return null;
        }
    }

    private async Task<VirtualNetworkResource?> TryGetVirtualNetwork(
        string clClusterName,
        ResourceGroupResource resourceGroupResource)
    {
        var vnetName = this.ToVnetName(clClusterName);

        try
        {
            VirtualNetworkResource resource =
                await resourceGroupResource.GetVirtualNetworkAsync(vnetName);
            return resource;
        }
        catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
        {
            // Does not exist.
            return null;
        }
    }

    private async Task<List<ContainerServiceManagedClusterResource>> GetManagedClusters(
        string clClusterName,
        ResourceGroupResource resourceGroupResource)
    {
        var collection = resourceGroupResource.GetContainerServiceManagedClusters();
        List<ContainerServiceManagedClusterResource> resources = new();
        await foreach (var item in collection.GetAllAsync())
        {
            if (item.Data.Tags.TryGetValue(CleanRoomClusterTag, out var clusterNameTagValue) &&
                clusterNameTagValue == clClusterName)
            {
                resources.Add(item);
            }
        }

        return resources;
    }

    private async Task<List<VirtualNetworkResource>> GetVirtualNetworks(
        string clClusterName,
        ResourceGroupResource resourceGroupResource)
    {
        var collection = resourceGroupResource.GetVirtualNetworks();
        List<VirtualNetworkResource> resources = new();
        await foreach (var item in collection.GetAllAsync())
        {
            if (item.Data.Tags.TryGetValue(CleanRoomClusterTag, out var clusterNameTagValue) &&
                clusterNameTagValue == clClusterName)
            {
                resources.Add(item);
            }
        }

        return resources;
    }

    private async Task<List<PrivateDnsZoneResource>> GetPrivateDnsZones(
        string clClusterName,
        ResourceGroupResource resourceGroupResource)
    {
        var collection = resourceGroupResource.GetPrivateDnsZones();
        List<PrivateDnsZoneResource> resources = new();
        await foreach (var item in collection.GetAllAsync())
        {
            if (item.Data.Tags.TryGetValue(CleanRoomClusterTag, out var clusterNameTagValue) &&
                clusterNameTagValue == clClusterName)
            {
                resources.Add(item);
            }
        }

        return resources;
    }

    private async Task<List<UserAssignedIdentityResource>> GetManagedIdentities(
        string clClusterName,
        ResourceGroupResource resourceGroupResource)
    {
        var collection = resourceGroupResource.GetUserAssignedIdentities();
        List<UserAssignedIdentityResource> resources = new();
        await foreach (var item in collection.GetAllAsync())
        {
            if (item.Data.Tags.TryGetValue(CleanRoomClusterTag, out var clusterNameTagValue) &&
                clusterNameTagValue == clClusterName)
            {
                resources.Add(item);
            }
        }

        return resources;
    }

    private CleanRoomCluster ToCleanRoomCluster(
        string clClusterName,
        VirtualNetworkResource vnet,
        ContainerServiceManagedClusterResource aks,
        bool analyticsWorkloadEnabled,
        string? analyticsAgentEndpoint,
        bool observabilityEnabled,
        string? observabilityEndpoint)
    {
        return new CleanRoomCluster
        {
            Name = clClusterName,
            InfraType = this.InfraType.ToString(),
            ObservabilityProfile = new ObservabilityProfile
            {
                Enabled = observabilityEnabled,
                VisualizationEndpoint = observabilityEndpoint,
                LogsEndpoint = observabilityEnabled ? Constants.LokiServiceEndpoint : null,
                TracesEndpoint = observabilityEnabled ? Constants.TempoServiceEndpoint : null,
                MetricsEndpoint = observabilityEnabled ? Constants.PrometheusServiceEndpoint : null
            },
            AnalyticsWorkloadProfile = new AnalyticsWorkloadProfile
            {
                Enabled = analyticsWorkloadEnabled,
                Namespace = analyticsWorkloadEnabled ? Constants.AnalyticsWorkloadNamespace : null,
                Endpoint = analyticsAgentEndpoint
            },
            ProviderProperties = new JsonObject
            {
                ["vnetId"] = vnet.Id.ToString(),
                ["aksClusterId"] = aks.Id.ToString(),
                ["kubernetesMasterFqdn"] = aks.Data.Fqdn
            }
        };
    }

    private async Task EnableAnalyticsWorkloadAsync(
        ArmClient client,
        string clClusterName,
        ResourceGroupResource resourceGroupResource,
        string kubeConfigFile,
        ContainerServiceManagedClusterResource aks,
        VirtualNetworkResource vnet,
        AnalyticsWorkloadProfileInput input,
        bool forceCreate,
        bool noWaitOnReady,
        IProgress<string> progressReporter)
    {
        // Create the analytics namespace and private dns zone for the same in which (a) the
        // spark CRs will get submitted (b) the driver and executor pods will run (c)
        // the driver pod service FQDN tied to that ns gets updated into the corresponding zone
        // via external-dns.
        string ns = Constants.AnalyticsWorkloadNamespace;
        await this.CreateNamespaceAsync(ns, kubeConfigFile);
        await this.CreateSparkOperatorServiceAccountRbac(ns, kubeConfigFile);
        progressReporter.Report("Creating private dns zone for analytics...");
        var privateZone = await this.CreatePrivateDNSZoneAsync(
            resourceGroupResource,
            clClusterName,
            Constants.AnalyticsWorkloadZoneName,
            forceCreate);

        progressReporter.Report("Creating private dns link for analytics...");
        await this.CreatePrivateDnsLinkAsync(clClusterName, privateZone, vnet, forceCreate);

        progressReporter.Report("Installing cleanroom-spark-frontend...");
        await this.InstallSparkFrontendOnAksAsync(
            resourceGroupResource,
            clClusterName,
            aks,
            vnet,
            input,
            kubeConfigFile,
            forceCreate);

        progressReporter.Report("Creating Public IP resource...");
        (var publicIP, string dnsLabel) = await SetupPublicIPForAnalyticsEndpoint();
        progressReporter.Report("Installing cleanroom-spark-analytics-agent...");
        await this.InstallAnalyticsAgentOnAksAsync(
            aks,
            input,
            publicIP,
            dnsLabel,
            kubeConfigFile);

        if (noWaitOnReady)
        {
            this.logger.LogInformation(
                "Not waiting on spark analytics agent or frontend pods/deployments to be ready " +
                "as noWaitOnReady was specified.");
        }
        else
        {
            progressReporter.Report("Waiting for cleanroom-spark-frontend to become ready...");
            await this.WaitForSparkFrontendUp(kubeConfigFile);
            progressReporter.Report(
                "Waiting for cleanroom-spark-analytics-agent to become ready...");
            await this.WaitForAnalyticsAgentUp(kubeConfigFile);
        }

        async Task<(PublicIPAddressResource publicIP, string dnsLabel)>
            SetupPublicIPForAnalyticsEndpoint()
        {
            // https://learn.microsoft.com/en-us/azure/aks/static-ip
            ResourceIdentifier nodeRgId = ResourceGroupResource.CreateResourceIdentifier(
                aks.Id.SubscriptionId,
                aks.Data.NodeResourceGroup);
            ResourceGroupResource nodeResourceGroupResource =
                client.GetResourceGroupResource(nodeRgId);
            string ipName = "analytics-agent-ip";
            var publicIP = await this.CreatePublicIP(
                nodeResourceGroupResource,
                aks.Data.Location,
                clClusterName,
                ipName,
                forceCreate);
            await this.UpdateAksMiPermissionsForPublicIPMgmtAsync(nodeResourceGroupResource, aks);
            string dnsLabel = this.GenerateDnsName(
                prefix: "analytics",
                clClusterName,
                nodeResourceGroupResource);
            return (publicIP, dnsLabel);
        }
    }

    private async Task<PrivateDnsZoneResource> CreatePrivateDNSZoneAsync(
        ResourceGroupResource resourceGroupResource,
        string clClusterName,
        string zoneName,
        bool forceCreate)
    {
        try
        {
            var zone = await resourceGroupResource.GetPrivateDnsZoneAsync(zoneName);
            this.logger.LogInformation(
                $"Found existing private dns zone so skipping creation: {zoneName}");
            return zone;
        }
        catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
        {
            // Does not exist. Proceed to creation.
        }

        PrivateDnsZoneCollection collection = resourceGroupResource.GetPrivateDnsZones();
        this.logger.LogInformation($"Starting private dns zone creation: {zoneName}");
        var data = new PrivateDnsZoneData("global")
        {
            Tags =
                {
                    {
                        CleanRoomClusterTag,
                        clClusterName
                    }
                },
        };

        ArmOperation<PrivateDnsZoneResource> lro = await collection.CreateOrUpdateAsync(
            WaitUntil.Completed,
            zoneName,
            data);
        PrivateDnsZoneResource result = lro.Value;
        PrivateDnsZoneData resourceData = result.Data;

        this.logger.LogInformation(
            $"Private dns zone creation succeeded. id: {resourceData.Id}");
        return result;
    }

    private async Task<VirtualNetworkLinkResource> CreatePrivateDnsLinkAsync(
        string clClusterName,
        PrivateDnsZoneResource privateZone,
        VirtualNetworkResource vnet,
        bool forceCreate)
    {
        var linkName = this.ToLinkName(privateZone.Data.Name);
        if (!forceCreate)
        {
            try
            {
                var link = await privateZone.GetVirtualNetworkLinkAsync(linkName);
                this.logger.LogInformation(
                    $"Found existing private dns link so skipping creation: {linkName}");
                return link;
            }
            catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                // Does not exist. Proceed to creation.
            }
        }

        this.logger.LogInformation($"Starting private dns link creation: {linkName}");
        var virtualNetworkLinkData = new VirtualNetworkLinkData("global")
        {
            Tags =
                {
                    {
                        CleanRoomClusterTag,
                        clClusterName
                    }
                },
            VirtualNetworkId = vnet.Id,
            RegistrationEnabled = false
        };

        ArmOperation<VirtualNetworkLinkResource> lro =
            await privateZone.GetVirtualNetworkLinks().CreateOrUpdateAsync(
                WaitUntil.Completed,
                linkName,
                virtualNetworkLinkData);
        VirtualNetworkLinkResource result = lro.Value;
        VirtualNetworkLinkData resourceData = result.Data;

        this.logger.LogInformation(
            $"Private dns link creation succeeded. id: {resourceData.Id}");
        return result;
    }

    private async Task<PublicIPAddressResource> CreatePublicIP(
        ResourceGroupResource nodeResourceGroupResource,
        string location,
        string clClusterName,
        string ipName,
        bool forceCreate)
    {
        if (!forceCreate)
        {
            try
            {
                var publicIP = await nodeResourceGroupResource.GetPublicIPAddressAsync(ipName);
                this.logger.LogInformation(
                    $"Found existing public IP resource so skipping creation: {ipName}");
                return publicIP;
            }
            catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
            {
                // Does not exist. Proceed to creation.
            }
        }

        PublicIPAddressCollection collection = nodeResourceGroupResource.GetPublicIPAddresses();
        this.logger.LogInformation($"Starting public IP address creation: {ipName}");
        var data = new PublicIPAddressData
        {
            Location = location,
            Tags =
            {
                {
                    CleanRoomClusterTag,
                    clClusterName
                }
            },
            PublicIPAllocationMethod = NetworkIPAllocationMethod.Static,
            PublicIPAddressVersion = NetworkIPVersion.IPv4,
            Sku = new PublicIPAddressSku
            {
                Name = PublicIPAddressSkuName.Standard
            }
        };

        ArmOperation<PublicIPAddressResource> lro = await collection.CreateOrUpdateAsync(
            WaitUntil.Completed,
            ipName,
            data);
        PublicIPAddressResource result = lro.Value;
        PublicIPAddressData resourceData = result.Data;

        this.logger.LogInformation($"Public IP creation succeeded. id: {resourceData.Id}");
        return result;
    }

    private async Task UpdateAksMiPermissionsForPublicIPMgmtAsync(
        ResourceGroupResource resourceGroupResource,
        ContainerServiceManagedClusterResource aks)
    {
        string networkContributorRoleDefinitionId =
            $"/subscriptions/{resourceGroupResource.Id.SubscriptionId}/providers/" +
            $"Microsoft.Authorization/roleDefinitions/4d97b98b-1d4f-4787-a291-c67834d212e7";
        string roleAssignmentId = Guid.NewGuid().ToString();

        var roleAssignmentData =
            new RoleAssignmentCreateOrUpdateContent(
                new ResourceIdentifier(networkContributorRoleDefinitionId),
                aks.Data.ClusterIdentity.PrincipalId!.Value)
            {
                PrincipalType = "ServicePrincipal",
            };

        var collection = resourceGroupResource.GetRoleAssignments();
        try
        {
            await collection.CreateOrUpdateAsync(
                WaitUntil.Completed,
                roleAssignmentId,
                roleAssignmentData);
        }
        catch (RequestFailedException rfe) when (rfe.ErrorCode == "RoleAssignmentExists")
        {
            // Already exists. Ignore failure.
        }

        this.logger.LogInformation(
            $"Network contributor role assignment over {resourceGroupResource.Id.Name} succeeded.");
    }

    private async Task<(string, SecurityPolicyDocument)> DownloadAndExpandAnalyticsAgentPolicy(
        SecurityPolicyCreationOption policyCreationOption,
        AgentChartValues values)
    {
        var policyDocument =
            await ImageUtils.GetAnalyticsAgentSecurityPolicyDocument(
                this.logger,
                this.configuration);

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
        var caType = !string.IsNullOrEmpty(values.CcrgovEndpoint) ? "cgs" : "local";
        var discovery = values.CcrgovServiceCertDiscovery ?? new ServiceCertDiscoveryInput();
        policyRego = policyRego.Replace("$caType", caType);
        policyRego = policyRego.Replace("$cgsEndpoint", values.CcrgovEndpoint);
        policyRego = policyRego.Replace("$ccrgovApiPathPrefix", values.CcrgovApiPathPrefix);
        policyRego = policyRego.Replace("$serviceCertBase64", values.CcrgovServiceCert);
        policyRego = policyRego.Replace("$serviceCertDiscoveryEndpoint", discovery.Endpoint);
        policyRego = policyRego.Replace("$serviceCertDiscoverySnpHostData", discovery.SnpHostData);
        policyRego = policyRego.Replace(
            "$serviceCertDiscoverySkipDigestCheck",
            discovery.SkipDigestCheck.ToString().ToLower());
        policyRego = policyRego.Replace(
            "$serviceCertDiscoveryConstitutionDigest",
            discovery.ConstitutionDigest);
        policyRego = policyRego.Replace(
            "$serviceCertDiscoveryJsappBundleDigest",
            discovery.JsappBundleDigest);
        policyRego = policyRego.Replace("$sparkFrontendEndpoint", values.SparkFrontendEndpoint);
        policyRego = policyRego.Replace(
            "$sparkFrontendSnpHostData",
            values.SparkFrontendSnpHostData);
        policyRego = policyRego.Replace(
            "$ccfNetworkRecoveryMembers",
            values.CcfNetworkRecoveryMembers);
        policyRego = policyRego.Replace(
            "$telemetryCollectionEnabled", values.TelemetryCollectionEnabled.ToString().ToLower());
        policyRego = policyRego.Replace("$prometheusEndpoint", values.PrometheusEndpoint);
        policyRego = policyRego.Replace("$lokiEndpoint", values.LokiEndpoint);
        policyRego = policyRego.Replace("$tempoEndpoint", values.TempoEndpoint);

        return (policyRego, policyDocument);
    }

    private async Task<(string, SecurityPolicyDocument)> DownloadAndExpandSparkFrontendPolicy(
        SecurityPolicyCreationOption policyCreationOption,
        bool telemetryCollectionEnabled)
    {
        var policyDocument =
            await ImageUtils.GetSparkFrontendSecurityPolicyDocument(
                this.logger,
                this.configuration);

        foreach (var container in policyDocument.Containers)
        {
            container.Image = container.Image.Replace("@@RegistryUrl@@", ImageUtils.RegistryUrl());
        }

        var policyRego =
            policyCreationOption == SecurityPolicyCreationOption.cachedDebug ?
            policyDocument.RegoDebug :
            policyCreationOption == SecurityPolicyCreationOption.cached ? policyDocument.Rego :
                throw new ArgumentException($"Unexpected option: {policyCreationOption}");

        policyRego = policyRego.Replace(
            "$telemetryCollectionEnabled", telemetryCollectionEnabled.ToString().ToLower());
        policyRego = policyRego.Replace(
            "$prometheusEndpoint",
            telemetryCollectionEnabled ?
            $"{Constants.PrometheusServiceEndpoint}:9090/api/v1/write" :
            string.Empty);
        policyRego = policyRego.Replace(
            "$lokiEndpoint",
            telemetryCollectionEnabled ?
            $"{Constants.LokiServiceEndpoint}:3100/otlp" :
            string.Empty);
        policyRego = policyRego.Replace(
            "$tempoEndpoint",
            telemetryCollectionEnabled ?
            $"{Constants.TempoServiceEndpoint}:4317" :
            string.Empty);
        return (policyRego, policyDocument);
    }

    private string GenerateDnsName(
        string prefix,
        string clClusterName,
        ResourceGroupResource resourceGroupResource)
    {
        string subscriptionId = resourceGroupResource.Id.SubscriptionId!;
        string resourceGroupName = resourceGroupResource.Id.Name;
        string uniqueString =
            Utils.GetUniqueString((subscriptionId + resourceGroupName + clClusterName).ToLower());
        string suffix = "-" + uniqueString;
        string dnsName = prefix + suffix;
        if (dnsName.Length > 63)
        {
            // DNS label cannot exceed 63 characters.
            dnsName = dnsName.Substring(0, 63 - suffix.Length) + suffix;
        }

        return dnsName;
    }

    private string ToVnetName(string input)
    {
        return input + "-vnet";
    }

    private string ToAksName(string input)
    {
        return input + "-aks";
    }

    private string ToLinkName(string input)
    {
        return input + "-link";
    }

    private string ToPolicyDigest(string policyRego)
    {
        return BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(policyRego)))
        .Replace("-", string.Empty).ToLower();
    }

    private string ToPolicyBase64(string policyRego)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(policyRego));
    }
}