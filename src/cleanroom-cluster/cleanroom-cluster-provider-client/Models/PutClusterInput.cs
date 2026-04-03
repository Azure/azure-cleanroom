// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CleanRoomProvider;

namespace Controllers;

public class PutClusterInput
{
    public InfraType InfraType { get; set; }

    public ObservabilityProfileInput? ObservabilityProfile { get; set; }

    public MonitoringProfileInput? MonitoringProfile { get; set; }

    public AnalyticsWorkloadProfileInput? AnalyticsWorkloadProfile { get; set; }

    public InferencingWorkloadProfileInput? InferencingWorkloadProfile { get; set; }

    public FlexNodeProfileInput? FlexNodeProfile { get; set; }

    public AadProfileInput? AadProfile { get; set; }

    public JsonObject? ProviderConfig { get; set; }
}