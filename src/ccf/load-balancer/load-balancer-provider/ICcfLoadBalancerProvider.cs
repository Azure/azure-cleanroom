// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace LoadBalancerProvider;

public interface ICcfLoadBalancerProvider
{
    Task<LoadBalancerEndpoint> CreateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
        JsonObject? providerConfig);

    Task DeleteLoadBalancer(string networkName, JsonObject? providerConfig);

    string GenerateLoadBalancerFqdn(
        string lbName,
        string networkName,
        JsonObject? providerConfig);

    Task<LoadBalancerEndpoint> GetLoadBalancerEndpoint(
        string networkName,
        JsonObject? providerConfig);

    Task<LoadBalancerEndpoint?> TryGetLoadBalancerEndpoint(
        string networkName,
        JsonObject? providerConfig);

    Task<LoadBalancerEndpoint> UpdateLoadBalancer(
        string lbName,
        string networkName,
        List<string> servers,
        JsonObject? providerConfig);

    Task<LoadBalancerHealth> GetLoadBalancerHealth(
        string networkName,
        JsonObject? providerConfig);
}