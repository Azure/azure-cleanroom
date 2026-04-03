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
        CcfClientManager clientManager,
        TimeSpan? timeout = null)
    {
        return WaitTransactionCommittedAsync("app/tx", response, clientManager, timeout);
    }

    internal static Task WaitGovTransactionCommittedAsync(
        this HttpResponseMessage response,
        CcfClientManager clientManager,
        TimeSpan? timeout = null)
    {
        return WaitTransactionCommittedAsync("gov/tx", response, clientManager, timeout);
    }

    private static async Task WaitTransactionCommittedAsync(
        string endpoint,
        HttpResponseMessage response,
        CcfClientManager clientManager,
        TimeSpan? timeout = null)
    {
        string? transactionId = GetTransactionIdHeaderValue(response);
        if (string.IsNullOrEmpty(transactionId))
        {
            return;
        }

        timeout ??= TimeSpan.FromSeconds(5);
        var status =
            await TrackTransactionStatusAsync(endpoint, clientManager, transactionId, timeout.Value);
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
            transactionId = values.First();
        }

        return transactionId;
    }

    private static async Task<string> TrackTransactionStatusAsync(
        string endpoint,
        CcfClientManager clientManager,
        string transactionId,
        TimeSpan timeout)
    {
        var ccfClient = await clientManager.GetAppClient();
        string transactionUrl = $"{endpoint}?transaction_id={transactionId}";
        var endTime = DateTimeOffset.Now + timeout;
        var getResponse = (await ccfClient.GetFromJsonAsync<JsonObject>(transactionUrl))!;
        var status = getResponse["status"]!.ToString();
        while ((status == "Unknown" || status == "Pending") && DateTimeOffset.Now <= endTime)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            getResponse = (await ccfClient.GetFromJsonAsync<JsonObject>(transactionUrl))!;
            status = getResponse["status"]!.ToString();
        }

        return status;
    }
}