// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using FrontendService.Models;

namespace FrontendSvc.Models;

public class GetDocumentResponse
{
    public required string Id { get; set; }

    public string? Version { get; set; }

    public required string State { get; set; }

    public required string ContractId { get; set; }

    public string? Data { get; set; }

    public JsonObject? Labels { get; set; }

    public List<UserProposalApprover>? Approvers { get; set; }

    public string? ProposalId { get; set; }

    public string? ProposerId { get; set; }

    public List<UserVote>? FinalVotes { get; set; }
}