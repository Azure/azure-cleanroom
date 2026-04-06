// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class CreateUserDocument
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("contractId")]
    public required string ContractId { get; set; }

    [JsonPropertyName("data")]
    public required string Data { get; set; }

    [JsonPropertyName("labels")]
    public required Dictionary<string, string> Labels { get; set; }

    [JsonPropertyName("approvers")]
    public required List<UserProposalApprover> Approvers { get; set; }
}
