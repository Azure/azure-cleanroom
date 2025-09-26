// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class CleanRoomClusterInput
{
    public ObservabilityProfileInput? ObservabilityProfile { get; set; }

    public AnalyticsWorkloadProfileInput? AnalyticsWorkloadProfile { get; set; }
}