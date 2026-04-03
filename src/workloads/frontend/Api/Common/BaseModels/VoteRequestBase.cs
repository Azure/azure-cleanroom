// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Api.Common.BaseModels;

/// <summary>
/// Base class for vote request across API versions.
/// </summary>
public abstract class VoteRequestBase
{
    /// <summary>
    /// Gets or sets the proposal ID to vote on.
    /// </summary>
    public string ProposalId { get; set; } = default!;

    /// <summary>
    /// Gets or sets the vote action (accept/reject).
    /// </summary>
    public string VoteAction { get; set; } = default!;
}
