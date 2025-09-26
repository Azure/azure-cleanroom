// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record JobPolicy(
    [property: JsonPropertyName("driver")] PodPolicy Driver,
    [property: JsonPropertyName("executor")] PodPolicy Executor);

public record PodPolicy(
    [property: JsonPropertyName("rego")] string Rego,
    [property: JsonPropertyName("regoBase64")] string RegoBase64,
    [property: JsonPropertyName("hostData")] string HostData);
