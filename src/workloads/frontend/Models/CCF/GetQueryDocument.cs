// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Controllers;

namespace FrontendSvc.Models.CCF;

public class GetQueryDocument : GetDocument<QueryDetails>
{
    [JsonPropertyName("proposalId")]
    public string ProposalId { get; set; } = string.Empty;

    [JsonPropertyName("approvers")]
    public List<UserProposalApprover> Approvers { get; set; } = [];

    public static GetQueryDocument FromDocumentResponse(GetDocumentResponse documentResponse)
    {
        if (string.IsNullOrWhiteSpace(documentResponse.Data))
        {
            throw new ApiException(
            HttpStatusCode.BadRequest,
            new ODataError(
                "InvalidQuerySpecification",
                "Invalid query specification."));
        }

        var sparkApplicationSpecification =
            JsonSerializer.Deserialize<SparkApplicationSpecification>(
                documentResponse.Data);

        if (sparkApplicationSpecification == null)
        {
            throw new ApiException(
            HttpStatusCode.BadRequest,
            new ODataError(
                "InvalidQuerySpecification",
                "Invalid query specification json format."));
        }

        return new GetQueryDocument
        {
            ProposalId = documentResponse.ProposalId ?? string.Empty,
            Approvers = documentResponse.Approvers ?? [],
            Id = documentResponse.Id,
            State = documentResponse.State,
            ProposerId = documentResponse.ProposerId ?? string.Empty,
            Version = documentResponse.Version ?? string.Empty,
            Data = QueryDetails.FromSparkSQLApplication(
                sparkApplicationSpecification.Application),
        };
    }
}
