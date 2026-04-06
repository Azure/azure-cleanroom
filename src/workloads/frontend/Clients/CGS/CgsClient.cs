// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Controllers;
using FrontendSvc.Models;
using FrontendSvc.Models.CCF;
using FrontendSvc.Utils.Encryption;
using static System.Net.Mime.MediaTypeNames;

namespace FrontendSvc.CGSClient;

public static class CgsClient
{
    private const string CcfEndpointHeaderKey = "x-ms-ccf-endpoint";
    private const string ServiceCertHeaderKey = "x-ms-service-cert";
    private const string AuthModeHeaderKey = "x-ms-auth-mode";
    private static readonly string ODataErrorCode = "CgsRequestFailed";

    public static Task<GetContractResponse> GetContractAsync(
        this CollaborationDetails collaborationDetails,
        string contractId,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<GetContractResponse>(
                $"/contracts/{contractId}",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<SetSecretResponse> SetSecretAsync(
        this CollaborationDetails collaborationDetails,
        string contractId,
        string secretName,
        string secretConfig,
        ILogger logger)
    {
        var content = new JsonObject
        {
            ["value"] = secretConfig
        };

        var requestBodyContent = new StringContent(
            content.ToJsonString(),
            Encoding.UTF8,
            Application.Json);

        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPutAsync<SetSecretResponse>(
                $"/contracts/{contractId}/secrets/{secretName}",
                logger,
                headers: GetHeaders(collaborationDetails),
                requestBodyContent),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task VoteDocumentProposalAsync(
        this CollaborationDetails collaborationDetails,
        string documentId,
        string proposalId,
        string vote,
        ILogger logger)
    {
        string path = vote == "accept" ? $"/userdocuments/{documentId}/vote_accept" :
            $"/userdocuments/{documentId}/vote_reject";

        var content = new JsonObject
        {
            ["proposalId"] = proposalId
        };

        var requestBody = new StringContent(
            content.ToJsonString(),
            Encoding.UTF8,
            Application.Json);

        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPostAsync(
                path,
                logger,
                headers: GetHeaders(collaborationDetails),
                requestBody),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<OidcIssuerInfoResponse> GetOidcIssuerInfoAsync(
        this CollaborationDetails collaborationDetails,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<OidcIssuerInfoResponse>(
                "/oidc/issuerInfo",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task SetIssuerUrl(
        this CollaborationDetails collaborationDetails,
        string issuerUrl,
        ILogger logger)
    {
        var body = JsonContent.Create(new { Url = issuerUrl });
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPostAsync(
                "/oidc/setIssuerUrl",
                logger,
                headers: GetHeaders(collaborationDetails),
                body),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<GetAuditEventsResponse> GetAuditEventsAsync(
        this CollaborationDetails collaborationDetails,
        string contractId,
        ILogger logger,
        string? scope = null,
        string? fromSeqno = null,
        string? toSeqno = null)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(scope))
        {
            queryParams.Add($"scope={Uri.EscapeDataString(scope)}");
        }

        if (!string.IsNullOrEmpty(fromSeqno))
        {
            queryParams.Add($"from_seqno={Uri.EscapeDataString(fromSeqno)}");
        }

        if (!string.IsNullOrEmpty(toSeqno))
        {
            queryParams.Add($"to_seqno={Uri.EscapeDataString(toSeqno)}");
        }

        string query = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : string.Empty;
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<GetAuditEventsResponse>(
                $"/contracts/{contractId}/events{query}",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<ListDocumentResponse> ListDatasetsAsync(
        this CollaborationDetails collaborationDetails,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<ListDocumentResponse>(
                "/userdocuments?labelSelector=type%3Adataset",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<ListDocumentResponse> ListQueriesAsync(
        this CollaborationDetails collaborationDetails,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<ListDocumentResponse>(
                "/userdocuments?labelSelector=type%3Aspark-application",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<GetDocumentResponse> GetDocumentAsync(
        this CollaborationDetails collaborationDetails,
        string documentId,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<GetDocumentResponse>(
                $"/userdocuments/{documentId}",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static async Task<TResult?> GetDocumentAsync<TResult>(
        this CollaborationDetails collaborationDetails,
        string documentId,
        ILogger logger)
        where TResult : class
    {
        GetDocumentResponse documentResponse = await GetDocumentAsync(
            collaborationDetails,
            documentId,
            logger);
        if (documentResponse.Data == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "ContentEmpty",
                    message: $"Document with Id {documentId} has null content."));
        }

        try
        {
            string? dataString = documentResponse.Data;
            TResult? result = JsonSerializer.Deserialize<TResult>(dataString);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                $"Failed to parse document {documentId}: {ex.Message}.");
            return null;
        }
    }

    public static async Task<ListDocumentResponse> ListQueriesbyDatasetAsync(
        this CollaborationDetails collaborationDetails,
        string datasetId,
        ILogger logger)
    {
        var allQueryDocuments = await collaborationDetails.ListQueriesAsync(
            logger);
        var queriesUsingDataset = new List<DocumentItem>();
        foreach (var query in allQueryDocuments.Value)
        {
            // TODO: Implement server-side filtering for queries by dataset.
            var queryData = await collaborationDetails.GetDocumentAsync<
                SparkApplicationSpecification>(query.Id, logger);
            if (queryData?.Application?.InputDataset != null &&
                queryData.Application.InputDataset.Any(
                ds => ds.Specification == datasetId))
            {
                queriesUsingDataset.Add(query);
            }

            if (queryData?.Application?.OutputDataset != null &&
                queryData.Application.OutputDataset.Specification == datasetId)
            {
                queriesUsingDataset.Add(query);
            }
        }

        logger.LogInformation(
            $"Found {queriesUsingDataset.Count} queries using dataset {datasetId}");

        return new ListDocumentResponse { Value = queriesUsingDataset };
    }

    public static Task<ConsentStatusResponse> CheckDocumentExecutionStatusAsync(
        this CollaborationDetails collaborationDetails,
        string documentId,
        ILogger logger)
    {
        var requestBody = new StringContent("{}", Encoding.UTF8, "application/json");

        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPostAsync<ConsentStatusResponse>(
                $"/userdocuments/{documentId}/checkstatus/execution",
                logger,
                headers: GetHeaders(collaborationDetails),
                requestBody),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task SetDocumentConsentAsync(
        this CollaborationDetails collaborationDetails,
        string documentId,
        string consentAction,
        ILogger logger)
    {
        if (consentAction != "enable" && consentAction != "disable")
        {
            throw new ArgumentException(
                "Action must be 'enable' or 'disable'",
                nameof(consentAction));
        }

        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPostAsync(
                $"/userdocuments/{documentId}/runtimeoptions/execution/{consentAction}",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static async Task<SkrPolicyResponse> GetSkrPolicyAsync(
        this CollaborationDetails collaborationDetails,
        string contractId,
        string datasetId,
        ILogger logger)
    {
        // Get the raw CGS response which contains policy object and proposalIds.
        var cleanroompolicy = await HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<CleanroomPolicyResponse>(
                $"/contracts/{contractId}/cleanroompolicy",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);

        // Get MAA authority from the specified dataset's KEK metadata.
        string? maaAuthority = await GetMaaAuthorityFromDatasetAsync(
            collaborationDetails,
            datasetId,
            logger);

        // Transform CGS response to SKR policy structure.
        return TransformToSkrPolicy(cleanroompolicy, maaAuthority, logger);
    }

    public static async Task<string?> GetMaaAuthorityFromDatasetAsync(
        CollaborationDetails collaborationDetails,
        string datasetId,
        ILogger logger)
    {
        try
        {
            // Fetch the raw dataset document from CGS.
            var documentResponse = await collaborationDetails.GetDocumentAsync(
                datasetId,
                logger);

            if (documentResponse == null)
            {
                logger.LogWarning($"Dataset {datasetId} not found");
                return null;
            }

            // Transform the document response to get DatasetDetails.
            // This deserializes DatasetSpecification and converts it to DatasetDetails,
            // which automatically extracts the MaaUrl from the KEK configuration.
            var datasetDocument = GetDatasetDocument.FromDatasetDetails(documentResponse);

            // Access the MaaUrl directly from the KEK encryption secret.
            if (datasetDocument.Data?.Kek?.MaaUrl != null)
            {
                logger.LogInformation(
                    $"Found MAA authority from dataset {datasetId}: " +
                    $"{datasetDocument.Data.Kek.MaaUrl}");
                return datasetDocument.Data.Kek.MaaUrl;
            }

            logger.LogWarning($"No MAA authority found in dataset {datasetId} KEK metadata");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, $"Failed to retrieve MAA authority from dataset {datasetId}");
            return null;
        }
    }

    public static SkrPolicyResponse TransformToSkrPolicy(
        CleanroomPolicyResponse cleanroompolicy,
        string? maaAuthority,
        ILogger logger)
    {
        // Extract security policy components from CGS response.
        // The x-ms-sevsnpvm-hostdata is typically an array in the CGS policy.
        string hostData;
        if (cleanroompolicy.Policy.TryGetValue("x-ms-sevsnpvm-hostdata", out var hostDataElement))
        {
            if (hostDataElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                hostData = hostDataElement[0].GetString()
                    ?? throw new InvalidOperationException("x-ms-sevsnpvm-hostdata[0] is null");
            }
            else
            {
                hostData = hostDataElement.GetString()
                    ?? throw new InvalidOperationException("x-ms-sevsnpvm-hostdata is null");
            }
        }
        else
        {
            throw new InvalidOperationException("x-ms-sevsnpvm-hostdata not found in policy");
        }

        // Build SKR policy structure.
        var skrPolicy = new SkrPolicyResponse
        {
            AnyOf = new()
            {
                new()
                {
                    AllOf = new()
                    {
                        new()
                        {
                            Claim = "x-ms-sevsnpvm-hostdata",
                            EqualsValue = hostData
                        },
                        new()
                        {
                            Claim = "x-ms-compliance-status",
                            EqualsValue = "azure-compliant-uvm"
                        },
                        new()
                        {
                            Claim = "x-ms-attestation-type",
                            EqualsValue = "sevsnpvm"
                        }
                    },
                    Authority = maaAuthority ?? string.Empty
                }
            },
            Version = "1.0.0"
        };

        logger.LogInformation(
            "Transformed CGS deployment policy to SKR policy structure. " +
            $"Policy hash: {hostData.Substring(0, Math.Min(32, hostData.Length))}..., " +
            $"Authority: {maaAuthority}");

        return skrPolicy;
    }

    public static Task<GetDeploymentInfoResponse> GetDeploymentInfoAsync(
        this CollaborationDetails collaborationDetails,
        string contractId,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<GetDeploymentInfoResponse>(
                $"/contracts/{contractId}/deploymentinfo",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<Proposal> ProposeUserDocumentAsync(
        this CollaborationDetails collaborationDetails,
        string documentId,
        JsonObject userDocumentProposal,
        ILogger logger)
    {
        var userDocumentProposalJsonString = userDocumentProposal.ToJsonString();

        var requestBody = new StringContent(
            userDocumentProposalJsonString,
            Encoding.UTF8,
            Application.Json);

        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPostAsync<Proposal>(
                $"/userdocuments/{documentId}/propose",
                logger,
                headers: GetHeaders(collaborationDetails),
                requestBody),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task CreateUserDocumentAsync(
        this CollaborationDetails collaborationDetails,
        string documentId,
        JsonObject userDocument,
        ILogger logger)
    {
        var requestBody = new StringContent(
            userDocument.ToJsonString(),
            Encoding.UTF8,
            Application.Json);

        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPutAsync(
                $"/userdocuments/{documentId}",
                logger,
                headers: GetHeaders(collaborationDetails),
                requestBody),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<GetUserDocument> GetUserDocumentAsync(
        this CollaborationDetails collaborationDetails,
        string documentId,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<GetUserDocument>(
                $"/userdocuments/{documentId}",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static async Task AcceptInvitationAsync(
       this CollaborationDetails collaborationDetails,
       string invitationId,
       ILogger logger)
    {
        try
        {
            await HttpClientUtilities.PerformHttpCallWithErrorHandling(
                httpClient => httpClient.HttpPostAsync(
                    $"/users/invitations/{invitationId}/accept",
                    logger,
                    headers: GetHeaders(collaborationDetails)),
                collaborationDetails.CgsClient,
                ODataErrorCode);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogWarning(
                ex,
                "Received conflict when accepting invitation {InvitationId}. " +
                "Invitation may already be accepted.",
                invitationId);
        }
    }

    public static Task<ListInvitationsResponse> ListInvitationsAsync(
        this CollaborationDetails collaborationDetails,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<ListInvitationsResponse>(
                "/users/invitations",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    public static Task<GetInvitationResponse> GetInvitationAsync(
        this CollaborationDetails collaborationDetails,
        string invitationId,
        ILogger logger)
    {
        return HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<GetInvitationResponse>(
                $"/users/invitations/{invitationId}",
                logger,
                headers: GetHeaders(collaborationDetails)),
            collaborationDetails.CgsClient,
            ODataErrorCode);
    }

    private static Dictionary<string, string?> GetHeaders(
        CollaborationDetails collaborationDetails)
    {
        return new Dictionary<string, string?>
        {
            [CcfEndpointHeaderKey] = collaborationDetails.ConsortiumEndpoint,
            [ServiceCertHeaderKey] = GetServiceCertBase64(
                collaborationDetails.ConsortiumServiceCertificatePem),
            [AuthModeHeaderKey] = "FromAuthHeader",
            ["Authorization"] = $"Bearer {collaborationDetails.UserToken}"
        };
    }

    private static string GetServiceCertBase64(string serviceCert)
    {
        return Base64.Encode(serviceCert);
    }
}