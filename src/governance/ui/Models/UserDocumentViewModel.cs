// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using static CgsUI.Controllers.MembersController;
using static CgsUI.Controllers.UsersController;

namespace CgsUI.Models;

public class UserDocumentViewModel
{
    public string Id { get; set; } = default!;

    public string ContractId { get; set; } = default!;

    public string Version { get; set; } = default!;

    public string State { get; set; } = default!;

    public object Data { get; set; } = default!;

    public string ProposalId { get; set; } = default!;

    public List<ApproversViewModel> Approvers { get; set; } = default!;

    public List<FinalVotesViewModel> FinalVotes { get; set; } = default!;

    public class FinalVotesViewModel
    {
        public string ApproverId { get; set; } = default!;

        public string Ballot { get; set; } = default!;
    }

    public class ApproversViewModel
    {
        public string ApproverId { get; set; } = default!;

        public string ApproverIdType { get; set; } = default!;
    }
}

public class UserDocumentViewWithMembersUsersModel
{
    public UserDocumentViewModel Document { get; set; } = default!;

    public ListUsers Users { get; set; } = default!;

    public ListMembers Members { get; set; } = default!;
}
