// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Governance.Models;

public class Proposal
{
    [JsonPropertyName("proposalId")]
    public string ProposalId { get; set; } = default!;

    [JsonPropertyName("proposalState")]
    public string ProposalState { get; set; } = default!;
}
