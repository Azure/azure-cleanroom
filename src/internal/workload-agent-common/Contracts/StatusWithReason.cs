// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record StatusWithReason(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] StatusReason Reason);

public record StatusReason(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
