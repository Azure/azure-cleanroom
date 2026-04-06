// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class Policy
{
    [JsonPropertyName("policy")]
    public JsonObject? PolicyDetails { get; set; }
}
