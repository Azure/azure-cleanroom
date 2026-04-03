// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Nodes;
using Controllers;

namespace CcfConsortiumMgr.Models;

public class GenerateWorkloadContractInput
{
    public required string CcfEndpoint { get; set; }

    public required string CcfServiceCertPem { get; set; }

    public required string RecoveryAgentEndpoint { get; set; }

    public required string RecoveryServiceEndpoint { get; set; }

    public required JsonObject CcfProviderConfig { get; set; }

    public required WorkloadType WorkloadType { get; set; }

    public required string ContractId { get; set; }

    public required string PolicyCreationOption { get; set; }

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

        if (this.CcfProviderConfig == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.CcfProviderConfig)} is missing.");
        }

        if (string.IsNullOrEmpty(this.ContractId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.ContractId)} is missing.");
        }

        if (string.IsNullOrEmpty(this.PolicyCreationOption))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "RequiredInputMissing",
                $"{nameof(this.PolicyCreationOption)} is missing.");
        }
    }
}
