// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record QueryDocument(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("datasets")] Dictionary<string, string> Datasets,
    [property: JsonPropertyName("datasink")] string Datasink);
