// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class CleanRoomClusterInput
{
    public ObservabilityProfileInput? ObservabilityProfile { get; set; }

    public MonitoringProfileInput? MonitoringProfile { get; set; }

    public AnalyticsWorkloadProfileInput? AnalyticsWorkloadProfile { get; set; }

    public InferencingWorkloadProfileInput? InferencingWorkloadProfile { get; set; }

    public FlexNodeProfileInput? FlexNodeProfile { get; set; }

    public AadProfileInput? AadProfile { get; set; }
}