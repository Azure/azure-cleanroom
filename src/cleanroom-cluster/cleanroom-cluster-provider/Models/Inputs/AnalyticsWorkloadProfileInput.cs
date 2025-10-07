// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class AnalyticsWorkloadProfileInput
{
    public bool Enabled { get; set; }

    public TelemetryProfileInput? TelemetryProfile { get; set; }

    public SecurityPolicyConfigInput? SecurityPolicy { get; set; }

    public string? ConfigurationUrl { get; set; }

    public string? ConfigurationUrlCaCert { get; set; }
}