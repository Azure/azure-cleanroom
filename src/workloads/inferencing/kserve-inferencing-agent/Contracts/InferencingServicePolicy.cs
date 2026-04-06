// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record InferencingServicePolicy(
    [property: JsonPropertyName("predictor")] PodPolicy Predictor,
    [property: JsonPropertyName("transformer")] PodPolicy Transformer);

public record PodPolicy(
    [property: JsonPropertyName("jsonBase64")] string JsonBase64,
    [property: JsonPropertyName("pcrs")] Dictionary<string, string> Pcrs);
