// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Node.Models;

public class NodeQuote
{
    [JsonPropertyName("endorsements")]
    public string Endorsements { get; set; } = default!;

    [JsonPropertyName("format")]
    public string Format { get; set; } = default!;

    [JsonPropertyName("mrenclave")]
    public string Mrenclave { get; set; } = default!;

    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = default!;

    [JsonPropertyName("raw")]
    public string Raw { get; set; } = default!;
}