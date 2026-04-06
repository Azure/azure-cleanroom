// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Models;

public class Proposal
{
    public int BallotCount { get; set; }

    public Dictionary<string, bool> FinalVotes { get; set; } = default!;

    public string ProposalId { get; set; } = default!;

    public string ProposalState { get; set; } = default!;

    public string ProposerId { get; set; } = default!;

    public Dictionary<string, bool> VoteFailures { get; set; } = default!;
}
