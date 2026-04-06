// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record Query(
    [property: JsonPropertyName("segments")] List<QuerySegment> Segments);

public record QuerySegment(
    [property: JsonPropertyName("executionSequence")] int ExecutionSequence,
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("preConditions")] List<PreCondition> PreConditions,
    [property: JsonPropertyName("postFilters")] List<PostFiltering> PostFilters);

public record PreCondition(
    [property: JsonPropertyName("viewName")] string ViewName,
    [property: JsonPropertyName("minRowCount")] int MinRowCount);

public record PostFiltering(
    [property: JsonPropertyName("columnName")] string ColumnName,
    [property: JsonPropertyName("value")] int Value);