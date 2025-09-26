// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace CleanRoomProvider;

public class CleanRoomCluster
{
    public string Name { get; set; } = default!;

    public string InfraType { get; set; } = default!;

    public ObservabilityProfile? ObservabilityProfile { get; set; } = default!;

    public AnalyticsWorkloadProfile? AnalyticsWorkloadProfile { get; set; } = default!;

    public JsonObject? ProviderProperties { get; set; }
}