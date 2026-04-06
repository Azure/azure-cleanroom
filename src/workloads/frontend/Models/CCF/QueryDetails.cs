// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using FrontendSvc.Utils.Encryption;

namespace FrontendSvc.Models.CCF;

public class QueryDetails
{
    [JsonPropertyName("queryData")]
    public required List<QueryData> QueryData { get; set; }

    [JsonPropertyName("inputDatasets")]
    public required string InputDatasets { get; set; }

    [JsonPropertyName("outputDataset")]
    public required string OutputDataset { get; set; }

    public static QueryDetails FromSparkSQLApplication(SparkSQLApplication sparkSQLApplication)
    {
        var inputDatasets = sparkSQLApplication.InputDataset
            .Select(GetDatasetString);
        var inputDatasetsString = string.Join(",", inputDatasets);

        var outputDatasetString = GetDatasetString(sparkSQLApplication.OutputDataset);

        return new QueryDetails
        {
            QueryData = GetQueryData(sparkSQLApplication.Query),
            InputDatasets = inputDatasetsString,
            OutputDataset = outputDatasetString,
        };
    }

    private static string GetDatasetString(SparkApplicationDatasetDescriptor dataset)
    {
        return $"{dataset.Specification}:{dataset.View}";
    }

    private static List<QueryData> GetQueryData(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var queryData = new List<QueryData>();

        var jsonStringQuery = Base64.Decode(query);
        var queryObject = JsonSerializer.Deserialize<Query>(jsonStringQuery);

        queryObject?.Segments.ForEach(segment =>
        {
            queryData.Add(CCF.QueryData.FromQuerySegment(segment));
        });

        return queryData;
    }
}
