// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class QuerySegment
{
    [JsonPropertyName("executionSequence")]
    public required int ExecutionSequence { get; set; }

    [JsonPropertyName("data")]
    public required string Data { get; set; }

    [JsonPropertyName("preConditions")]
    public List<PreCondition> PreConditions { get; set; } = [];

    [JsonPropertyName("postFilters")]
    public List<PostFilter> PostFilters { get; set; } = [];
}
