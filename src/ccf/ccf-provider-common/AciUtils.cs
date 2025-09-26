// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.Resources;
using LoadBalancerProvider;

namespace CcfProvider;

public static class AciUtils
{
    public static NodeEndpoint ToNodeEndpoint(ContainerGroupData data)
    {
        if (!data.Tags.TryGetValue(AciConstants.CcfNetworkResourceNameTag, out var nodeName))
        {
            nodeName = data.Name;
        }

        return new NodeEndpoint
        {
            NodeName = nodeName,
            ClientRpcAddress = $"{data.IPAddress.Fqdn}:{Ports.RpcMainPort}",
            NodeEndorsedRpcAddress = $"{data.IPAddress.Fqdn}:{Ports.RpcDebugPort}"
        };
    }

    public static NodeHealth ToNodeHealth(ContainerGroupData data)
    {
        if (!data.Tags.TryGetValue(AciConstants.CcfNetworkResourceNameTag, out var nodeName))
        {
            nodeName = data.Name;
        }

        NodeStatus status = NodeStatus.Ok;
        var reasons = new List<Reason>();
        var restartedContainers = data.Containers.Where(
            c => c.InstanceView?.RestartCount > 0);
        var terminatedContainers = data.Containers.Where(
            c => c.InstanceView?.CurrentState?.State == "Terminated");
        var multipleStartedEventContainers = data.Containers.Where(
            c => c.InstanceView?.Events?.Count(e => e.Name == "Started") > 1);

        if (restartedContainers.Any())
        {
            status = NodeStatus.NeedsReplacement;
            var code = "ContainerRestarted";
            var message = "Following container(s) are reporting a restart count > 0.";
            foreach (var c in restartedContainers)
            {
                message += $" {c.Name}: {c.InstanceView.RestartCount}.";
            }

            reasons.Add(new() { Code = code, Message = message });
        }

        if (terminatedContainers.Any())
        {
            status = NodeStatus.NeedsReplacement;
            var code = "ContainerTerminated";
            var message = "Following container(s) are reporting as terminated.";
            foreach (var c in terminatedContainers)
            {
                message += $" {c.Name}.";
            }

            reasons.Add(new() { Code = code, Message = message });
        }

        if (data.ProvisioningState == "Failed")
        {
            status = NodeStatus.NeedsReplacement;
            var code = "ProvisioningFailed";
            var message = $"The container group {data.Name} is reporting provisioning failure.";
            reasons.Add(new() { Code = code, Message = message });
        }

        if (data.InstanceView?.State == "Stopped")
        {
            status = NodeStatus.NeedsReplacement;
            var code = "ContainerGroupStopped";
            var message = $"The container group {data.Name} is reporting state as stopped.";
            reasons.Add(new() { Code = code, Message = message });
        }

        if (multipleStartedEventContainers.Any())
        {
            status = NodeStatus.NeedsReplacement;
            var code = "MultipleStartedEvents";
            var message = "Following container(s) are reporting multiple Started events.";
            foreach (var c in multipleStartedEventContainers)
            {
                message += $" {c.Name}: {c.InstanceView.Events.Count(e => e.Name == "Started")}.";
            }

            reasons.Add(new() { Code = code, Message = message });
        }

        var ep = ToNodeEndpoint(data);
        return new NodeHealth
        {
            Name = nodeName,
            Endpoint = ep.ClientRpcAddress,
            Status = status.ToString(),
            Reasons = reasons
        };
    }

    public static LoadBalancerEndpoint ToLbEndpoint(ContainerGroupData data)
    {
        if (!data.Tags.TryGetValue(
            AciConstants.CcfNetworkResourceNameTag,
            out var nameTagValue))
        {
            nameTagValue = "NotSet";
        }

        return new LoadBalancerEndpoint
        {
            Name = nameTagValue,
            Endpoint = $"https://{data.IPAddress.Fqdn}:{Ports.LbMainPort}",
            AgentEndpoint = $"https://{data.IPAddress.Fqdn}:{Ports.LbAgentPort}"
        };
    }

    public static LoadBalancerHealth ToLbHealth(ContainerGroupData data)
    {
        LbStatus status = LbStatus.Ok;
        var reasons = new List<LoadBalancerProvider.Reason>();
        if (data.ProvisioningState == "Failed")
        {
            status = LbStatus.NeedsReplacement;
            var code = "ProvisioningFailed";
            var message = $"The container group {data.Name} is reporting provisioning failure.";
            reasons.Add(new() { Code = code, Message = message });
        }
        else if (data.InstanceView?.State == "Stopped")
        {
            status = LbStatus.NeedsReplacement;
            var code = "ContainerGroupStopped";
            var message = $"The container group {data.Name} is reporting state as stopped.";
            reasons.Add(new() { Code = code, Message = message });
        }

        var ep = ToLbEndpoint(data);
        return new LoadBalancerHealth
        {
            Name = ep.Name,
            Endpoint = ep.Endpoint,
            Status = status.ToString(),
            Reasons = reasons
        };
    }

    public static RecoveryAgentEndpoint ToRecoveryAgentEndpoint(ContainerGroupData data)
    {
        if (!data.Tags.TryGetValue(AciConstants.CcfNetworkResourceNameTag, out var name))
        {
            name = data.Name;
        }

        return new RecoveryAgentEndpoint
        {
            Name = name,
            Endpoint = $"https://{data.IPAddress.Fqdn}:{Ports.EnvoyPort}"
        };
    }

    public static Task<List<ContainerGroupResource>> GetNetworkContainerGroups(
    string serviceName,
    string type,
    JsonObject? providerConfig)
    {
        return GetContainerGroups(
            serviceName,
            AciConstants.CcfNetworkNameTag,
            type,
            AciConstants.CcfNetworkTypeTag,
            providerConfig);
    }

    public static Task<List<ContainerGroupResource>> GetRecoveryServiceContainerGroups(
    string serviceName,
    string type,
    JsonObject? providerConfig)
    {
        return GetContainerGroups(
            serviceName,
            AciConstants.CcfRecoveryServiceNameTag,
            type,
            AciConstants.CcfRecoveryServiceTypeTag,
            providerConfig);
    }

    public static Task<List<ContainerGroupResource>> GetConsortiumManagerContainerGroups(
        string consortiumManagerName,
        string type,
        JsonObject? providerConfig)
    {
        return GetContainerGroups(
            consortiumManagerName,
            AciConstants.CcfConsortiumManagerNameTag,
            type,
            AciConstants.CcfConsortiumManagerTypeTag,
            providerConfig);
    }

    public static async Task<ContainerGroupData?> TryGetContainerGroupData(
        string containerGroupName,
        JsonObject providerConfig)
    {
        var result = await TryGetContainerGroup(containerGroupName, providerConfig);
        if (result != null)
        {
            return result.Data;
        }

        return null;
    }

    public static async Task<ContainerGroupResource?> TryGetContainerGroup(
        string containerGroupName,
        JsonObject providerConfig)
    {
        try
        {
            var cg = await GetContainerGroup(containerGroupName, providerConfig);
            return cg;
        }
        catch (Azure.RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public static async Task<ContainerGroupResource> GetContainerGroup(
        string containerGroupName,
        JsonObject providerConfig)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);
        var cg = await resourceGroupResource.GetContainerGroupAsync(containerGroupName);
        return cg.Value;
    }

    private static async Task<List<ContainerGroupResource>> GetContainerGroups(
        string serviceName,
        string serviceNameTag,
        string type,
        string typeTag,
        JsonObject? providerConfig)
    {
        var client = new ArmClient(new DefaultAzureCredential());
        string subscriptionId = providerConfig!["subscriptionId"]!.ToString();
        string resourceGroupName = providerConfig["resourceGroupName"]!.ToString();
        ResourceIdentifier resourceGroupResourceId =
            ResourceGroupResource.CreateResourceIdentifier(subscriptionId, resourceGroupName);
        ResourceGroupResource resourceGroupResource =
            client.GetResourceGroupResource(resourceGroupResourceId);

        ContainerGroupCollection collection = resourceGroupResource.GetContainerGroups();
        List<ContainerGroupResource> containerGroups = new();
        await foreach (var item in collection.GetAllAsync())
        {
            if (item.Data.Tags.TryGetValue(serviceNameTag, out var nameTagValue) &&
                nameTagValue == serviceName &&
                item.Data.Tags.TryGetValue(typeTag, out var nwTypeValue) &&
                nwTypeValue == type)
            {
                // Get each item explicitly or else the instanceView property is not populated.
                ContainerGroupResource fetchedItem = await item.GetAsync();
                containerGroups.Add(fetchedItem);
            }
        }

        return containerGroups;
    }
}
