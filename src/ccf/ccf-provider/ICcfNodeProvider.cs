// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfCommon;

namespace CcfProvider;

public interface ICcfNodeProvider
{
    public InfraType InfraType { get; }

    public Task<NodeEndpoint> CreateStartNode(
        string nodeName,
        string networkName,
        List<InitialMember> initialMember,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        NodeData nodeData,
        List<string> san,
        JsonObject? providerConfig);

    public Task<NodeEndpoint> CreateJoinNode(
        string nodeName,
        string networkName,
        string serviceCertPem,
        string targetNodeName,
        string targetRpcAddress,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        NodeData nodeData,
        List<string> san,
        JsonObject? providerConfig);

    public Task<NodeEndpoint> CreateRecoverNode(
        string nodeName,
        string networkName,
        string networkToRecoverName,
        string previousServiceCertPem,
        string? nodeLogLevel,
        SecurityPolicyConfiguration policyOption,
        NodeData nodeData,
        List<string> san,
        JsonObject? providerConfig);

    Task<List<string>> GetCandidateRecoveryNodes(string networkName, JsonObject? providerConfig);

    Task DeleteNodes(string networkName, DeleteOption deleteOption, JsonObject? providerConfig);

    Task DeleteNode(
        string networkName,
        string nodeName,
        NodeData nodeData,
        JsonObject? providerConfig);

    Task<List<NodeEndpoint>> GetNodes(string networkName, JsonObject? providerConfig);

    Task<List<RecoveryAgentEndpoint>> GetRecoveryAgents(
        string networkName,
        JsonObject? providerConfig);

    Task<List<NodeHealth>> GetNodesHealth(string networkName, JsonObject? providerConfig);

    Task<NodeHealth> GetNodeHealth(string networkName, string nodeName, JsonObject? providerConfig);

    Task<JsonObject> GenerateSecurityPolicy(SecurityPolicyCreationOption policyOption);

    Task<JsonObject> GenerateJoinPolicy(SecurityPolicyCreationOption policyOption);
}