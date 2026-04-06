// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class UserDocumentProposal
{
    [JsonPropertyName("version")]
    public required string Version { get; set; }
}