// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record InferencingModelSpecification(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("application")] InferencingModelApplication Application);

public record InferencingModelApplication(
    [property: JsonPropertyName("applicationType")] string ApplicationType,
    [property: JsonPropertyName("modelDir")] string? ModelDir,
    [property: JsonPropertyName("inputDataset")]
    List<InferencingModelApplicationDatasetDescriptor> InputDataset);

public record InferencingModelApplicationDatasetDescriptor(
    [property: JsonPropertyName("specification")] string Specification);
