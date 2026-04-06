// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class DatasetSpecification
{
    [JsonPropertyName("name")]
    public required string Name { get; set; }

    [JsonPropertyName("datasetSchema")]
    public required DataSchema DatasetSchema { get; set; }

    [JsonPropertyName("datasetAccessPolicy")]
    public required DataAccessPolicy DatasetAccessPolicy { get; set; }

    [JsonPropertyName("datasetAccessPoint")]
    public required AccessPoint DatasetAccessPoint { get; set; }

    public static DatasetSpecification FromDatasetDetails(
        DatasetDetails datasetDetails)
    {
        return new DatasetSpecification
        {
            Name = datasetDetails.Name,
            DatasetSchema = datasetDetails.DatasetSchema,
            DatasetAccessPolicy = datasetDetails.DatasetAccessPolicy,
            DatasetAccessPoint = AccessPoint.FromDatasetDetails(
                datasetDetails),
        };
    }
}
