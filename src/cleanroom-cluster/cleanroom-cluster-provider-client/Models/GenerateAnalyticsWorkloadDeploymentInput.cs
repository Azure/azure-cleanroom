// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CleanRoomProvider;

namespace Controllers;

public class GenerateAnalyticsWorkloadDeploymentInput
{
    public InfraType InfraType { get; set; }

    public TelemetryProfileInput? TelemetryProfile { get; set; }

    public SecurityPolicyConfigInput? SecurityPolicy { get; set; }

    public string? ContractUrl { get; set; }

    public string? ContractUrlCaCert { get; set; }

    public JsonObject? ProviderConfig { get; set; }
}