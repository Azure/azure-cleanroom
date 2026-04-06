// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Models;

public class VoteRequest
{
    public string ProposalId { get; set; } = default!;

    public string VoteAction { get; set; } = default!;
}
