// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Controllers;

public static class HttpResponseMessageExtensions
{
    private const string TransactionIdHeader = "x-ms-ccf-transaction-id";

    internal static void CopyHeaders(
        this HttpResponse response,
        HttpResponseHeaders responseHeaders)
    {
        foreach (var header in responseHeaders)
        {
#pragma warning disable ASP0019 // Suggest using IHeaderDictionary.Append or the indexer
            response.Headers.Add(header.Key, header.Value.ToArray());
#pragma warning restore ASP0019 // Suggest using IHeaderDictionary.Append or the indexer
        }
    }

    internal static Task WaitAppTransactionCommittedAsync(
        this HttpResponseMessage response,
        ILogger logger,
        CcfClientManager clientManager,
        TimeSpan? timeout = null)
    {
        return WaitTransactionCommittedAsync("app/tx", logger, response, clientManager, timeout);
    }

    internal static Task WaitGovTransactionCommittedAsync(
        this HttpResponseMessage response,
        ILogger logger,
        CcfClientManager clientManager,
        TimeSpan? timeout = null)
    {
        return WaitTransactionCommittedAsync("gov/tx", logger, response, clientManager, timeout);
    }

    private static async Task WaitTransactionCommittedAsync(
        string endpoint,
        ILogger logger,
        HttpResponseMessage response,
        CcfClientManager clientManager,
        TimeSpan? timeout = null)
    {
        string? transactionId = GetTransactionIdHeaderValue(response);
        if (string.IsNullOrEmpty(transactionId))
        {
            return;
        }

        var ccfClient = clientManager.GetNoAuthClient();
        timeout ??= TimeSpan.FromSeconds(5);
        var status = await TrackTransactionStatusAsync(
            ccfClient,
            endpoint,
            logger,
            clientManager,
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
        HttpClient ccfClient,
        string endpoint,
        ILogger logger,
        CcfClientManager clientManager,
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
}