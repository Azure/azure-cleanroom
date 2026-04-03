// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class InferencingWorkloadProfileInput
{
    [JsonPropertyName("kserveProfile")]
    public KServeInferencingWorkloadProfileInput? KServeProfile { get; set; }
}

public class KServeInferencingWorkloadProfileInput
{
    public bool Enabled { get; set; }

    public SecurityPolicyConfigInput? SecurityPolicy { get; set; }

    public TelemetryProfileInput? TelemetryProfile { get; set; }

    public string? ConfigurationUrl { get; set; }

    public string? ConfigurationUrlCaCert { get; set; }

    public Dictionary<string, string>? ConfigurationUrlHeaders { get; set; }
}