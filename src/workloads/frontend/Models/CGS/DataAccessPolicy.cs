// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class DataAccessPolicy
{
    [JsonPropertyName("accessMode")]
    public required AccessMode AccessMode { get; set; }

    [JsonPropertyName("allowedFields")]
    public required List<string> AllowedFields { get; set; }
}
