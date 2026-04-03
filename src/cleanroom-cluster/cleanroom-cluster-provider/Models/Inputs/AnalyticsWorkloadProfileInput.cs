// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class AnalyticsWorkloadProfileInput
{
    public bool Enabled { get; set; }

    public TelemetryProfileInput? TelemetryProfile { get; set; }

    public SecurityPolicyConfigInput? SecurityPolicy { get; set; }

    public WorkloadPoolProfileInput? PoolProfile { get; set; }

    public string? ConfigurationUrl { get; set; }

    public string? ConfigurationUrlCaCert { get; set; }

    public Dictionary<string, string>? ConfigurationUrlHeaders { get; set; }
}

public class WorkloadPoolProfileInput
{
    public int NodeCount { get; set; }
}