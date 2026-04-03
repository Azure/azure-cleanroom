// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class GenerateKServeInferencingWorkloadDeploymentInput
{
    public SecurityPolicyConfigInput? SecurityPolicy { get; set; }

    public TelemetryProfileInput? TelemetryProfile { get; set; }

    public string ContractUrl { get; set; } = default!;

    public string? ContractUrlCaCert { get; set; } = default!;

    public Dictionary<string, string>? ContractUrlHeaders { get; set; } = default!;
}
