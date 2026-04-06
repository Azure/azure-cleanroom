// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class Query
{
    [JsonPropertyName("segments")]
    public required List<QuerySegment> Segments { get; set; }

    public static Query FromQueryData(List<QueryData> queryData)
    {
        var segments = new List<QuerySegment>();

        foreach (var segment in queryData)
        {
            segments.Add(CreateQuerySegment(segment));
        }

        return new Query
        {
            Segments = segments
        };
    }

    private static QuerySegment CreateQuerySegment(QueryData queryData)
    {
        var postFilters = new List<PostFilter>();
        var postFiltersStrings = queryData.PostFilters.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var postFilterString in postFiltersStrings)
        {
            postFilters.Add(PostFilter.FromString(postFilterString.Trim()));
        }

        var preConditions = new List<PreCondition>();
        var preConditionsStrings = queryData.PreConditions.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var preConditionString in preConditionsStrings)
        {
            preConditions.Add(PreCondition.FromString(preConditionString.Trim()));
        }

        return new QuerySegment
        {
            Data = queryData.Data,
            ExecutionSequence = queryData.ExecutionSequence,
            PostFilters = postFilters,
            PreConditions = preConditions,
        };
    }
}