// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfCommon;

namespace CcfRecoveryProvider;

public interface ICcfRecoveryServiceInstanceProvider
{
    public RsInfraType InfraType { get; }

    Task<RecoveryServiceEndpoint> CreateRecoveryService(
        string instanceName,
        string serviceName,
        string akvEndpoint,
        string maaEndpoint,
        string? managedIdentityId,
        NetworkJoinPolicy networkJoinPolicy,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig);

    Task DeleteRecoveryService(string serviceName, JsonObject? providerConfig);

    Task<RecoveryServiceEndpoint> GetRecoveryServiceEndpoint(
        string serviceName,
        JsonObject? providerConfig);

    Task<RecoveryServiceEndpoint?> TryGetRecoveryServiceEndpoint(
        string serviceName,
        JsonObject? providerConfig);

    Task<RecoveryServiceHealth> GetRecoveryServiceHealth(
        string serviceName,
        JsonObject? providerConfig);

    Task<JsonObject> GenerateSecurityPolicy(
        NetworkJoinPolicy joinPolicy,
        SecurityPolicyCreationOption policyOption);
}