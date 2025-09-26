// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CgsUI.Models;

public class UsersAndInvitationsViewModel
{
    public List<UserViewModel> Users { get; set; } = default!;

    public List<InvitationViewModel> OpenInvitations { get; set; } = default!;
}

public class UserViewModel
{
    public string UserId { get; set; } = default!;

    public string? UserName { get; set; } = default!;

    public string? InvitationId { get; set; } = default!;

    public string? InvitationStatus { get; set; } = default!;
}

public class InvitationViewModel
{
    public string? UserName { get; set; } = default!;

    public string? InvitationId { get; set; } = default!;

    public string? InvitationStatus { get; set; } = default!;
}
