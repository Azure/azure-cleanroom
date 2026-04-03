// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfConsortiumMgr.Clients.Governance.Models;

public class UserIdentity
{
    public required string TenantId { get; set; }

    public required string ObjectId { get; set; }

    public required AccountType AccountType { get; set; }

    public string? Identifier { get; set; }

    public string? InvitationId { get; set; }
}
