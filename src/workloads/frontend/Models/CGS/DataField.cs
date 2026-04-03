// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class DataField
{
    [JsonPropertyName("fieldName")]
    public required string FieldName { get; set; }

    [JsonPropertyName("fieldType")]
    public required DataFieldType FieldType { get; set; }
}
