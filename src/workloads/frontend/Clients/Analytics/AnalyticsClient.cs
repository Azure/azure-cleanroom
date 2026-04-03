// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Controllers;
using FrontendSvc.Models;

namespace FrontendSvc.AnalyticsClient;

public static class AnalyticsClient
{
    private const string AuthHeaderKey = "x-ms-cleanroom-authorization";
    private const string CorrelationIdHeaderKey = "x-ms-correlation-id";
    private const string ClientRequestIdHeaderKey = "x-ms-client-request-id";
    private static readonly string ODataErrorCode = "AnalyticsRequestFailed";

    public static async Task<QueryRunOutput> RunQueryAsync(
        this HttpClient analyticsClient,
        string queryId,
        string userToken,
        QueryRunInput requestBody,
        ILogger logger)
    {
        var correlationId = Guid.NewGuid().ToString();
        var clientRequestId = Guid.NewGuid().ToString();

        logger.LogInformation(
            $"Running query {queryId}. Correlation-ID: {correlationId}, " +
            $"Client-Request-ID: {clientRequestId}");
        var result = await HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpPostAsync<QueryRunOutput>(
                $"queries/{queryId}/run",
                logger,
                headers: GetHeaders(userToken, correlationId, clientRequestId),
                body: JsonContent.Create(requestBody)),
            analyticsClient,
            ODataErrorCode);

        result.CorrelationId = correlationId;
        result.ClientRequestId = clientRequestId;

        return result;
    }

    public static async Task<QueryRunResult> GetQueryRunResultAsync(
        this HttpClient analyticsClient,
        string jobId,
        string userToken,
        ILogger logger)
    {
        var correlationId = Guid.NewGuid().ToString();
        var clientRequestId = Guid.NewGuid().ToString();
        logger.LogInformation(
            $"Getting status for job {jobId}. Correlation-ID: {correlationId}, " +
            $"Client-Request-ID: {clientRequestId}");
        var result = await HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<QueryRunResult>(
                $"status/{jobId}",
                logger,
                GetHeaders(userToken, correlationId, clientRequestId)),
            analyticsClient,
            ODataErrorCode);

        result.CorrelationId = correlationId;
        result.ClientRequestId = clientRequestId;
        return result;
    }

    public static async Task<QueryRunHistory> GetQueryRunHistoryAsync(
        this HttpClient analyticsClient,
        string queryId,
        string userToken,
        ILogger logger)
    {
        var correlationId = Guid.NewGuid().ToString();
        var clientRequestId = Guid.NewGuid().ToString();
        logger.LogInformation(
            $"Getting query run history for query {queryId}. Correlation-ID: {correlationId}, " +
            $"Client-Request-ID: {clientRequestId}");
        return await HttpClientUtilities.PerformHttpCallWithErrorHandling(
            httpClient => httpClient.HttpGetAsync<QueryRunHistory>(
                $"queries/{queryId}/runs",
                logger,
                GetHeaders(userToken, correlationId, clientRequestId)),
            analyticsClient,
            ODataErrorCode);
    }

    private static Dictionary<string, string?> GetHeaders(
        string userToken,
        string correlationId,
        string clientRequestId)
    {
        return new Dictionary<string, string?>
        {
            [AuthHeaderKey] = $"Bearer {userToken}",
            [CorrelationIdHeaderKey] = correlationId,
            [ClientRequestIdHeaderKey] = clientRequestId
        };
    }
}