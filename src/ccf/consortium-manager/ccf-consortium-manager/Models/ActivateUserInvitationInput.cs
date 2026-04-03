// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class ActivateUserInvitationInput
{
    public required string CcfEndpoint { get; set; }

    public required string CcfServiceCertPem { get; set; }

    public required string InvitationId { get; set; }

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

        if (string.IsNullOrEmpty(this.InvitationId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.InvitationId)} is missing.");
        }
    }
}
