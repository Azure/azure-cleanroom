// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class ValidateConsortiumInput
{
    public required string CcfEndpoint { get; set; }

    public required string RecoveryAgentEndpoint { get; set; }

    public required string RecoveryServiceEndpoint { get; set; }

    public void Validate()
    {
        if (string.IsNullOrEmpty(this.CcfEndpoint))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.CcfEndpoint)} is missing.");
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
    }
}
