// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendService.Models;

public class UserVote
{
    [JsonPropertyName("approverId")]
    public string ApproverId { get; set; } = string.Empty;

    [JsonPropertyName("ballot")]
    public string Ballot { get; set; } = string.Empty;
}
