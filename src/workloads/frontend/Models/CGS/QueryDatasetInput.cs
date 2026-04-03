// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Serialization;
using Controllers;

namespace FrontendSvc.Models;

public class QueryDatasetInput
{
    [JsonPropertyName("view")]
    public required string View { get; set; }

    [JsonPropertyName("datasetDocumentId")]
    public required string DatasetDocumentId { get; set; }

    public static QueryDatasetInput FromString(string queryDatasetString)
    {
        var parts = queryDatasetString.Split(':');
        if (parts.Length != 2)
        {
            throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        "InvalidQueryDatasetFormat",
                        "Query dataset is expected to be a ':' separated string of the format " +
                        "<DatasetDocumentId>:<View>."));
        }

        return new QueryDatasetInput
        {
            DatasetDocumentId = parts[0].Trim(),
            View = parts[1].Trim(),
        };
    }
}