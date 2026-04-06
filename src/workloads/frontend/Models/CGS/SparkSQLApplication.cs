// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class SparkSQLApplication
{
    [JsonPropertyName("applicationType")]
    public required string ApplicationType { get; set; }

    [JsonPropertyName("inputDataset")]
    public required List<SparkApplicationDatasetDescriptor> InputDataset { get; set; }

    [JsonPropertyName("outputDataset")]
    public required SparkApplicationDatasetDescriptor OutputDataset { get; set; }

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;
}
