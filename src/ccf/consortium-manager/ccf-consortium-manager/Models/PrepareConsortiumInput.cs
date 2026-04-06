// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class PrepareConsortiumInput
{
    public required string CcfEndpoint { get; set; }

    public required string CcfServiceCertPem { get; set; }

    public required string RecoveryAgentEndpoint { get; set; }

    public required string RecoveryServiceEndpoint { get; set; }

    public required UserIdentity UserIdentity { get; set; }

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

        if (string.IsNullOrEmpty(this.RecoveryAgentEndpoint))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.RecoveryAgentEndpoint)} is missing.");
        }

        if (string.IsNullOrEmpty(this.RecoveryServiceEndpoint))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.RecoveryServiceEndpoint)} is missing.");
        }

        if (this.UserIdentity == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.UserIdentity)} is missing.");
        }
        else
        {
            this.UserIdentity.Validate();
        }
    }
}
