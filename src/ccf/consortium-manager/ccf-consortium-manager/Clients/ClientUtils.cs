// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Controllers;

namespace CcfConsortiumMgr.Clients;

internal static class ClientUtils
{
    public static async Task<T> SendRequest<T>(
        this HttpClient httpClient,
        ILogger logger,
        string requestPath,
        JsonObject? requestContent,
        HttpMethod? httpMethod = null,
        bool skipOutputLogging = false)
    {
        string responseContent =
            await SendRequestInternal(
                httpClient,
                logger,
                requestPath,
                requestContent,
                httpMethod,
                skipOutputLogging);
        return JsonSerializer.Deserialize<T>(responseContent)!;
    }

    public static async Task SendRequest(
        this HttpClient httpClient,
        ILogger logger,
        string requestPath,
        JsonObject? requestContent,
        HttpMethod? httpMethod = null,
        bool skipOutputLogging = false)
    {
        await SendRequestInternal(
            httpClient,
            logger,
            requestPath,
            requestContent,
            httpMethod,
            skipOutputLogging);
    }

    public static async Task<T> SendCoseRequest<T>(
        this HttpClient httpClient,
        ILogger logger,
        string requestPath,
        byte[] requestContent,
        bool skipOutputLogging = false)
    {
        string responseContent =
            await SendCoseRequestInternal(
                httpClient,
                logger,
                requestPath,
                requestContent,
                skipOutputLogging);
        return JsonSerializer.Deserialize<T>(responseContent)!;
    }

    public static async Task SendCoseRequest(
        this HttpClient httpClient,
        ILogger logger,
        string requestPath,
        byte[] requestContent,
        bool skipOutputLogging = false)
    {
        await SendCoseRequestInternal(
            httpClient,
            logger,
            requestPath,
            requestContent,
            skipOutputLogging);
    }

    private static async Task<string> SendRequestInternal(
        this HttpClient httpClient,
        ILogger logger,
        string requestPath,
        JsonObject? requestContent,
        HttpMethod? httpMethod = null,
        bool skipOutputLogging = false)
    {
        using var requestMessage =
            new HttpRequestMessage
            {
                Content = requestContent != null ?
                    new StringContent(
                        requestContent.ToJsonString(),
                        Encoding.UTF8,
                        MediaTypeNames.Application.Json) :
                    null,
                Method = httpMethod ?? HttpMethod.Post,
                RequestUri = new Uri(requestPath, UriKind.Relative)
            };

        using HttpResponseMessage response =
            await httpClient.SendAsync(requestMessage);
        await response.ValidateStatusCodeAsync(logger);

        var responseContent = await response.Content.ReadAsStringAsync();
        logger.LogInformation(
            $"Operation completed with StatusCode: {response.StatusCode}, " +
            $"ReasonPhrase: {response.ReasonPhrase}, " +
            $"ResponseContent: {(!skipOutputLogging ? responseContent : "<Redacted>")}.");

        return responseContent;
    }

    private static async Task<string> SendCoseRequestInternal(
        this HttpClient httpClient,
        ILogger logger,
        string requestPath,
        byte[] requestContent,
        bool skipOutputLogging = false)
    {
        using var requestMessage =
            new HttpRequestMessage
            {
                Content = new ByteArrayContent(requestContent),
                Method = HttpMethod.Post,
                RequestUri = new Uri(requestPath, UriKind.Relative)
            };
        requestMessage.Content.Headers.ContentType =
            new MediaTypeWithQualityHeaderValue("application/cose");

        using HttpResponseMessage response =
            await httpClient.SendAsync(requestMessage);
        await response.ValidateStatusCodeAsync(logger);

        var responseContent = await response.Content.ReadAsStringAsync();
        logger.LogInformation(
            $"Operation completed with StatusCode: {response.StatusCode}, " +
            $"ReasonPhrase: {response.ReasonPhrase}, " +
            $"ResponseContent: {(!skipOutputLogging ? responseContent : "<Redacted>")}.");

        return responseContent;
    }
}
