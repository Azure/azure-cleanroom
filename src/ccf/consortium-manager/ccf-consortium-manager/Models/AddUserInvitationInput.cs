// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class AddUserInvitationInput
{
    public required string CcfEndpoint { get; set; }

    public required string CcfServiceCertPem { get; set; }

    public required UserInvitation UserInvitation { get; set; }

    public void Validate()
    {
        if (string.IsNullOrEmpty(this.CcfEndpoint))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.CcfEndpoint)} is missing.");
        }

        if (string.IsNullOrEmpty(this.CcfServiceCertPem))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.CcfServiceCertPem)} is missing.");
        }

        if (this.UserInvitation == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.UserInvitation)} is missing.");
        }
        else
        {
            this.UserInvitation.Validate();
        }
    }
}
