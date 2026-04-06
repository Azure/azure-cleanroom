// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace CleanRoomProvider;

public class KServeInferencingWorkloadGeneratedDeployment
{
    public string SecurityPolicyCreationOption { get; set; } = default!;

    public KServeInferencingDeploymentTemplate DeploymentTemplate { get; set; } = default!;

    public GovernancePolicyOutput GovernancePolicy { get; set; } = default!;

    public CcePolicyOutput CcePolicy { get; set; } = default!;

    public class GovernancePolicyOutput
    {
        public string Type { get; set; } = default!;

        public string PolicyType { get; set; } = default!;

        public JsonObject Claims { get; set; } = default!;
    }

    public class CcePolicyOutput
    {
        public string Value { get; set; } = default!;

        public string DocumentUrl { get; set; } = default!;
    }
}
