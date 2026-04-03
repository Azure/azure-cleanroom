// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Node.Models;

public class QuotesList
{
    [JsonPropertyName("quotes")]
    public List<NodeQuote> Quotes { get; set; } = default!;
}