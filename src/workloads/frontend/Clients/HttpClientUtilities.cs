// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Controllers;
using static System.Net.Mime.MediaTypeNames;

namespace FrontendSvc;

public static class HttpClientUtilities
{
    public static Task<T> HttpGetAsync<T>(
        this HttpClient client,
        string requestPath,
        ILogger logger,
        Dictionary<string, string?>? headers = null,
        HttpContent? body = null)
    {
        return PerformHttpCall<T>(
            client,
            HttpMethod.Get,
            requestPath,
            logger,
            headers,
            body);
    }

    public static Task HttpPostAsync(
        this HttpClient client,
        string requestPath,
        ILogger logger,
        Dictionary<string, string?>? headers = null,
        HttpContent? body = null)
    {
        return PerformHttpCall(
            client,
            HttpMethod.Post,
            requestPath,
            logger,
            headers,
            body);
    }

    public static Task<T> HttpPostAsync<T>(
        this HttpClient client,
        string requestPath,
        ILogger logger,
        Dictionary<string, string?>? headers = null,
        HttpContent? body = null)
    {
        return PerformHttpCall<T>(
            client,
            HttpMethod.Post,
            requestPath,
            logger,
            headers,
            body);
    }

    public static Task HttpPutAsync(
        this HttpClient client,
        string requestPath,
        ILogger logger,
        Dictionary<string, string?>? headers = null,
        HttpContent? body = null)
    {
        return PerformHttpCall(
            client,
            HttpMethod.Put,
            requestPath,
            logger,
            headers,
            body);
    }

    public static Task<T> HttpPutAsync<T>(
        this HttpClient client,
        string requestPath,
        ILogger logger,
        Dictionary<string, string?>? headers = null,
        HttpContent? body = null)
    {
        return PerformHttpCall<T>(
            client,
            HttpMethod.Put,
            requestPath,
            logger,
            headers,
            body);
    }

    public static async Task<T> PerformHttpCallWithErrorHandling<T>(
        Func<HttpClient, Task<T>> function,
        HttpClient httpClient,
        string errorCode)
    {
        try
        {
            return await function.Invoke(httpClient);
        }
        catch (RequestFailedException ex)
        {
            var odataError = TryParseODataErrorFromJson(ex.Message, errorCode);
            throw new ApiException(
                (HttpStatusCode)ex.Status,
                odataError);
        }
        catch (Exception ex)
        {
            throw new ApiException(
                HttpStatusCode.InternalServerError,
                new ODataError(
                    errorCode,
                    ex.Message));
        }
    }

    public static async Task PerformHttpCallWithErrorHandling(
        Func<HttpClient, Task> function,
        HttpClient httpClient,
        string errorCode)
    {
        try
        {
            await function.Invoke(httpClient);
        }
        catch (RequestFailedException ex)
        {
            var odataError = TryParseODataErrorFromJson(ex.Message, errorCode);
            throw new ApiException(
                (HttpStatusCode)ex.Status,
                odataError);
        }
        catch (Exception ex)
        {
            throw new ApiException(
                HttpStatusCode.InternalServerError,
                new ODataError(
                    errorCode,
                    ex.Message));
        }
    }

    private static ODataError TryParseODataErrorFromJson(string content, string fallbackCode)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(content);
            if (jsonDoc.RootElement.TryGetProperty("error", out var errorElement))
            {
                string? code = null;
                string? message = null;

                if (errorElement.TryGetProperty("code", out var codeElement))
                {
                    code = codeElement.GetString();
                }

                if (errorElement.TryGetProperty("message", out var messageElement))
                {
                    message = messageElement.GetString();
                }

                if (!string.IsNullOrEmpty(code) && !string.IsNullOrEmpty(message))
                {
                    return new ODataError(code, message);
                }
            }
        }
        catch
        {
            // If parsing fails, fall through to use fallback.
        }

        return new ODataError(fallbackCode, content);
    }

    private static async Task PerformHttpCall(
        HttpClient client,
        HttpMethod httpMethod,
        string requestPath,
        ILogger logger,
        Dictionary<string, string?>? headers = null,
        HttpContent? body = null)
    {
        var method = httpMethod.Method;
        using HttpRequestMessage request = new(httpMethod, requestPath);
        if (headers != null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        request.Content = body ?? new StringContent("{}", Encoding.UTF8, Application.Json);

        var stopWatch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request);
            await response.ValidateStatusCodeAsync(logger);
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Error occurred while sending {method} request to {requestPath}: {ex.Message}");

            throw;
        }
        finally
        {
            stopWatch.Stop();

            logger.LogInformation(
                $"Completed {method} request to {requestPath}. " +
                $"Time taken: {stopWatch.ElapsedMilliseconds} ms");
        }
    }

    private static async Task<T> PerformHttpCall<T>(
        HttpClient client,
        HttpMethod httpMethod,
        string requestPath,
        ILogger logger,
        Dictionary<string, string?>? headers = null,
        HttpContent? body = null)
    {
        var method = httpMethod.Method;
        using HttpRequestMessage request = new(httpMethod, requestPath);
        if (headers != null)
        {
            foreach (var header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        request.Content = body ?? new StringContent("{}", Encoding.UTF8, Application.Json);

        var stopWatch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request);
            await response.ValidateStatusCodeAsync(logger);

            var jsonResponse = await response.Content.ReadFromJsonAsync<T>();
            return jsonResponse!;
        }
        catch (Exception ex)
        {
            logger.LogError(
                $"Error occurred while sending {method} request to {requestPath}: {ex.Message}");

            throw;
        }
        finally
        {
            stopWatch.Stop();

            logger.LogInformation(
                $"Completed {method} request to {requestPath}. " +
                $"Time taken: {stopWatch.ElapsedMilliseconds} ms");
        }
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
                $"reasonPhrase: {response.ReasonPhrase} and content: {content}. ");

            throw new Azure.RequestFailedException((int)response.StatusCode, content);
        }
    }
}
