// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Serialization;
using Controllers;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class PublishQueryInputDetails : BaseDataPublishInputDetails
{
    [JsonPropertyName("queryData")]
    public required Query QueryData { get; set; }

    [JsonPropertyName("inputDatasets")]
    public required List<QueryDatasetInput> InputDatasets { get; set; }

    [JsonPropertyName("outputDataset")]
    public required QueryDatasetInput OutputDataset { get; set; }

    public static PublishQueryInputDetails FromQueryDetails(QueryDetails queryDetails)
    {
        var inputDatasets = new List<QueryDatasetInput>();

        if (string.IsNullOrWhiteSpace(queryDetails.InputDatasets) ||
            string.IsNullOrWhiteSpace(queryDetails.OutputDataset))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    "InvalidQueryDatasetFormat",
                    "Input and output dataset values must be non-empty."));
        }

        var inputDatasetStrings = queryDetails.InputDatasets.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries);
        inputDatasetStrings.ToList()
            .ForEach(entry => inputDatasets.Add(QueryDatasetInput.FromString(entry.Trim())));

        var outputDataset = QueryDatasetInput.FromString(queryDetails.OutputDataset.Trim());

        return new PublishQueryInputDetails
        {
            QueryData = Query.FromQueryData(queryDetails.QueryData),
            InputDatasets = inputDatasets,
            OutputDataset = outputDataset,
        };
    }
}
