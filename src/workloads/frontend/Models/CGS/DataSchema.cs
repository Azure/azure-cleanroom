// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class DataSchema
{
    [JsonPropertyName("format")]
    public required DataFormat Format { get; set; }

    [JsonPropertyName("fields")]
    public required List<DataField> Fields { get; set; }
}
