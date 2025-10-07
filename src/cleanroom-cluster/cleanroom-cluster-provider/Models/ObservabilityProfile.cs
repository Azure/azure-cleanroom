// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class ObservabilityProfile
{
    public bool Enabled { get; set; } = default!;

    public string? MetricsEndpoint { get; set; }

    public string? LogsEndpoint { get; set; }

    public string? TracesEndpoint { get; set; }

    public string? VisualizationEndpoint { get; set; }
}