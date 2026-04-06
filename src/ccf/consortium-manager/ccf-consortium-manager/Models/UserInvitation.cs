// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class UserInvitation
{
    public required string InvitationId { get; set; }

    public string? TenantId { get; set; }

    public required IdentityType IdentityType { get; set; }

    public required string IdentityName { get; set; }

    public required AccountType AccountType { get; set; }

    public void Validate()
    {
        if (string.IsNullOrEmpty(this.InvitationId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.InvitationId)} is missing.");
        }

        if (string.IsNullOrEmpty(this.IdentityName))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.IdentityName)} is missing.");
        }

        if (this.IdentityType == IdentityType.ServicePrincipal &&
            string.IsNullOrEmpty(this.TenantId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.TenantId)} is missing.");
        }
    }
}
