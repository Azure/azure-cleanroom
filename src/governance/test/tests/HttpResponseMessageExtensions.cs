// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Rest.TransientFaultHandling;

namespace Test;

public static class HttpResponseMessageExtensions
{
    private const string TransactionIdHeader = "x-ms-ccf-transaction-id";

    internal static Task WaitAppTransactionCommittedAsync(
        this HttpResponseMessage response,
        ILogger logger,
        HttpClient ccfClient,
        TimeSpan? timeout = null)
    {
        return WaitTransactionCommittedAsync("app/tx", logger, response, ccfClient, timeout);
    }

    private static async Task WaitTransactionCommittedAsync(
        string endpoint,
        ILogger logger,
        HttpResponseMessage response,
        HttpClient ccfClient,
        TimeSpan? timeout = null)
    {
        string? transactionId = GetTransactionIdHeaderValue(response);
        if (string.IsNullOrEmpty(transactionId))
        {
            return;
        }

        timeout ??= TimeSpan.FromSeconds(5);
        var status = await TrackTransactionStatusAsync(
            endpoint,
            logger,
            ccfClient,
            transactionId,
            timeout.Value);
        if (status != "Committed")
        {
            throw new Exception($"Transaction failed to commit within {timeout}. Status: {status}.");
        }
    }

    private static string? GetTransactionIdHeaderValue(HttpResponseMessage response)
    {
        string? transactionId = null;
        if (response.Headers.TryGetValues(TransactionIdHeader, out var values))
        {
            transactionId = values!.First();
        }

        return transactionId;
    }

    private static async Task<string> TrackTransactionStatusAsync(
        string endpoint,
        ILogger logger,
        HttpClient ccfClient,
        string transactionId,
        TimeSpan timeout)
    {
        string transactionUrl = $"{endpoint}?transaction_id={transactionId}";
        var endTime = DateTimeOffset.Now + timeout;
        using var response1 = await ccfClient.GetAsync(transactionUrl);
        await response1.ValidateStatusCodeAsync(logger);
        var getResponse = (await response1.Content.ReadFromJsonAsync<JsonObject>())!;
        var status = getResponse["status"]!.ToString();
        while ((status == "Unknown" || status == "Pending") && DateTimeOffset.Now <= endTime)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            using var response2 = await ccfClient.GetAsync(transactionUrl);
            await response2.ValidateStatusCodeAsync(logger);
            getResponse = (await response2.Content.ReadFromJsonAsync<JsonObject>())!;
            status = getResponse["status"]!.ToString();
        }

        return status;
    }

    private static async Task ValidateStatusCodeAsync(
    this HttpResponseMessage response,
    ILogger logger)
    {
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();

            logger.LogError(
                $"{response.RequestMessage!.Method} request for resource: " +
                $"{response.RequestMessage.RequestUri} " +
                $"failed with statusCode {response.StatusCode}, " +
                $"reasonPhrase: {response.ReasonPhrase} and content: {content}.");

            throw new HttpRequestWithStatusException(content)
            {
                StatusCode = response.StatusCode
            };
        }
    }
}