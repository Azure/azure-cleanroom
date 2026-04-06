// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models.CCF;

public class QueryData
{
    [JsonPropertyName("postFilters")]
    public string PostFilters { get; set; } = string.Empty;

    [JsonPropertyName("preConditions")]
    public string PreConditions { get; set; } = string.Empty;

    [JsonPropertyName("executionSequence")]
    public required int ExecutionSequence { get; set; }

    [JsonPropertyName("data")]
    public required string Data { get; set; }

    public static QueryData FromQuerySegment(QuerySegment segment)
    {
        return new QueryData
        {
            PostFilters = GetPostFilters(segment.PostFilters ?? []),
            PreConditions = GetPreConditions(segment.PreConditions ?? []),
            ExecutionSequence = segment.ExecutionSequence,
            Data = segment.Data,
        };
    }

    private static string GetPostFilters(List<PostFilter> postFilters)
    {
        var postFiltersStringList = new List<string>();

        foreach (var postFilter in postFilters)
        {
            postFiltersStringList.Add($"{postFilter.ColumnName}:{postFilter.Value}");
        }

        return string.Join(",", postFiltersStringList);
    }

    private static string GetPreConditions(List<PreCondition> preConditions)
    {
        var preConditionsStringList = new List<string>();

        foreach (var preCondition in preConditions)
        {
            preConditionsStringList.Add($"{preCondition.ViewName}:{preCondition.MinRowCount}");
        }

        return string.Join(",", preConditionsStringList);
    }
}
