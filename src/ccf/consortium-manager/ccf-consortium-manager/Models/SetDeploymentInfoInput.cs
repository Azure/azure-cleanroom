// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Nodes;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class SetDeploymentInfoInput
{
    public required string CcfEndpoint { get; set; }

    public required string CcfServiceCertPem { get; set; }

    public required string ContractId { get; set; }

    public required JsonObject DeploymentInfo { get; set; }

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

        if (string.IsNullOrEmpty(this.ContractId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.ContractId)} is missing.");
        }

        if (this.DeploymentInfo == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.DeploymentInfo)} is missing.");
        }
    }
}
