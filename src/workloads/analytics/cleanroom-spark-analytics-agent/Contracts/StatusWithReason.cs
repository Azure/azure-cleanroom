// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public record StatusWithReason(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] ErrorResponse Reason);

public record ErrorResponse(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
