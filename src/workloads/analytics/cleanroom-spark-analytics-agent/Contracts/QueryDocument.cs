// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record SparkApplicationSpecification(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("application")] SparkSQLApplication Application);

public record SparkSQLApplication(
    [property: JsonPropertyName("applicationType")] string ApplicationType,
    [property: JsonPropertyName("inputDataset")]
    List<SparkApplicationDatasetDescriptor> InputDataset,
    [property: JsonPropertyName("outputDataset")] SparkApplicationDatasetDescriptor OutputDataset,
    [property: JsonPropertyName("query")] string Query);

public record SparkApplicationDatasetDescriptor(
    [property: JsonPropertyName("specification")] string Specification,
    [property: JsonPropertyName("view")] string View);
