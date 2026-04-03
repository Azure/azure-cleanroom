// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using FrontendService.Models;

namespace FrontendSvc.Models;

public class GetUserDocument : CreateUserDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("proposalId")]
    public string ProposalId { get; set; } = string.Empty;

    [JsonPropertyName("proposerId")]
    public string ProposerId { get; set; } = string.Empty;

    [JsonPropertyName("finalVotes")]
    public List<UserVote> FinalVotes { get; set; } = [];
}