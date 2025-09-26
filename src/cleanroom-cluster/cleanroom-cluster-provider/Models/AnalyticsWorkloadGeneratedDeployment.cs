// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class AnalyticsWorkloadGeneratedDeployment
{
    public string SecurityPolicyCreationOption { get; set; } = default!;

    public DeploymentTemplate DeploymentTemplate { get; set; } = default!;

    public GovernancePolicyOutput GovernancePolicy { get; set; } = default!;

    public CcePolicyOutput CcePolicy { get; set; } = default!;

    public class GovernancePolicyOutput
    {
        public string Type { get; set; } = default!;

        public ClaimsOutput Claims { get; set; } = default!;

        public class ClaimsOutput
        {
            [JsonPropertyName("x-ms-sevsnpvm-is-debuggable")]
            public bool IsDebuggable { get; set; }

            [JsonPropertyName("x-ms-sevsnpvm-hostdata")]
            public string HostData { get; set; } = default!;
        }
    }

    public class CcePolicyOutput
    {
        public string Value { get; set; } = default!;

        public string DocumentUrl { get; set; } = default!;
    }
}
