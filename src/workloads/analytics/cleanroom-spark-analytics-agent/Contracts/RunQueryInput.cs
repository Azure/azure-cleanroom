// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record RunQueryInput(
    [property: JsonPropertyName("runId")] string? RunId,
    [property: JsonPropertyName("startDate")] DateTimeOffset? StartDate,
    [property: JsonPropertyName("endDate")] DateTimeOffset? EndDate,
    [property: JsonPropertyName("useOptimizer")] bool UseOptimizer = false,
    [property: JsonPropertyName("dryRun")] bool DryRun = false);
