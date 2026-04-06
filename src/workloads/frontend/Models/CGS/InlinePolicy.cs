// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class InlinePolicy
{
    [JsonPropertyName("policyDocument")]
    public string PolicyDocument { get; set; } = string.Empty;
}
