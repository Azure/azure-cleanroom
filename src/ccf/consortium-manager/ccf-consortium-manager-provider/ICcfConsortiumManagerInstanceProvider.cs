// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfCommon;

namespace CcfConsortiumMgrProvider;

public interface ICcfConsortiumManagerInstanceProvider
{
    public CMInfraType InfraType { get; }

    Task<CcfConsortiumManagerEndpoint> CreateConsortiumManager(
        string instanceName,
        string consortiumManagerName,
        SecurityPolicyConfiguration policyOption,
        JsonObject? providerConfig);

    Task<CcfConsortiumManagerEndpoint?> TryGetConsortiumManagerEndpoint(
        string consortiumManagerName,
        JsonObject? providerConfig);
}
