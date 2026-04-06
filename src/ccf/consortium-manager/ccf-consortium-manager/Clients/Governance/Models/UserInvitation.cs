// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfConsortiumMgr.Clients.Governance.Models;

public class UserInvitation
{
    public required string InvitationId { get; set; }

    public string? TenantId { get; set; }

    public required IdentityType IdentityType { get; set; }

    public required string IdentityName { get; set; }

    public required AccountType AccountType { get; set; }
}
