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
    [property: JsonPropertyName("modelFormat")] InferencingModelFormat? ModelFormat,
    [property: JsonPropertyName("modelDatasets")]
    List<InferencingModelApplicationDatasetDescriptor> ModelDatasets,
    [property: JsonPropertyName("runtime")] InferencingModelApplicationRuntime? Runtime);

public record InferencingModelApplicationDatasetDescriptor(
    [property: JsonPropertyName("specification")] string Specification);

public record InferencingModelFormat(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version);

// Runtime is declared inline on the model document and pins the runtime
// the inferencing agent will deploy by name. The image+digest implementing
// that runtime is operational state pinned by the agent/frontend release
// version (resolved by the frontend from its bundled digest table), not
// user-supplied input — so it stays out of the governance contract.
public record InferencingModelApplicationRuntime(
    [property: JsonPropertyName("name")] string Name);
