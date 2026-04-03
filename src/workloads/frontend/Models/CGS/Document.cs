// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class Document
{
    [JsonPropertyName("documentType")]
    public required string DocumentType { get; set; }

    [JsonPropertyName("authenticityReceipt")]
    public required string AuthenticityReceipt { get; set; }

    [JsonPropertyName("identity")]
    public Identity? Identity { get; set; } = null;

    [JsonPropertyName("backingResource")]
    public required Resource BackingResource { get; set; }
}
