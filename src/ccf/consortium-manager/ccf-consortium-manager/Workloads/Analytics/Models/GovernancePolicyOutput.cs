// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class GovernancePolicyOutput
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = default!;

    [JsonPropertyName("policyType")]
    public string PolicyType { get; set; } = default!;

    [JsonPropertyName("claims")]
    public JsonObject Claims { get; set; } = default!;
}
