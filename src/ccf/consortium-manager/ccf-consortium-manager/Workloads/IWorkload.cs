// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace CcfConsortiumMgr.Workloads;

public interface IWorkload
{
    Task<(JsonObject, JsonObject)> GenerateDeploymentSpec(
        JsonObject contractData,
        string policyCreationOption);
}
