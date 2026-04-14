// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendSvc.Models.CCF;

namespace FrontendSvc.Models;

public class DataField
{
    [JsonPropertyName("fieldName")]
    [RequiredNotNullOrWhiteSpace]
    public required string FieldName { get; set; }

    [JsonPropertyName("fieldType")]
    public required DataFieldType FieldType { get; set; }
}
