// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc;
using OhttpClient;
using OhttpCommon;

namespace Controllers;

/// <summary>
/// Transparent reverse proxy controller. Accepts plain HTTP requests, wraps them
/// in OHTTP, forwards to the gateway, decapsulates the response, and returns
/// plain HTTP back to the caller.
/// </summary>
[ApiController]
public class ProxyController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly KeyConfigCache keyConfigCache;
    private readonly IHttpClientFactory httpClientFactory;

    public ProxyController(
        ILogger logger,
        IConfiguration configuration,
        KeyConfigCache keyConfigCache,
        IHttpClientFactory httpClientFactory)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.keyConfigCache = keyConfigCache;
        this.httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Routes: POST /{model}/v1/chat/completions — model is in the URL path.
    /// </summary>
    /// <param name="modelName">The model name from the URL path.</param>
    /// <returns>The decapsulated inner HTTP response.</returns>
    [HttpPost("/{modelName}/v1/chat/completions")]
    public async Task<IActionResult> ProxyWithModelInPath(string modelName)
    {
        return await this.ProxyRequest(modelName, "/v1/chat/completions");
    }

    /// <summary>
    /// Routes: POST /{model}/v2/models/{m}/infer — KServe v2 inference protocol.
    /// </summary>
    /// <param name="modelName">The model name from the URL path.</param>
    /// <param name="m">The model identifier for the v2 infer endpoint.</param>
    /// <returns>The decapsulated inner HTTP response.</returns>
    [HttpPost("/{modelName}/v2/models/{m}/infer")]
    public async Task<IActionResult> ProxyV2Infer(string modelName, string m)
    {
        return await this.ProxyRequest(modelName, $"/v2/models/{m}/infer");
    }

    private async Task<IActionResult> ProxyRequest(string modelName, string innerPath)
    {
        this.logger.LogInformation(
            "Proxying request for model '{ModelName}' path '{Path}'.",
            modelName,
            innerPath);

        // Read the request body.
        using var bodyStream = new MemoryStream();
        await this.Request.Body.CopyToAsync(bodyStream);
        byte[] requestBody = bodyStream.ToArray();

        // Collect headers to forward.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (this.Request.ContentType != null)
        {
            headers["content-type"] = this.Request.ContentType;
        }

        // Serialize as Binary HTTP request.
        byte[] binaryHttpRequest = BinaryHttp.SerializeRequest(
            this.Request.Method,
            "https",
            $"{modelName}-predictor-https.kserve-inferencing.svc",
            innerPath,
            headers,
            requestBody);

        // Get the KeyConfig and encapsulate.
        KeyConfig keyConfig;
        try
        {
            keyConfig = await this.keyConfigCache.GetKeyConfigAsync();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to fetch KeyConfig from OHTTP server.");
            throw;
        }

        var encapsulator = new OhttpEncapsulator(keyConfig);
        byte[] ohttpRequest = encapsulator.EncapsulateRequest(binaryHttpRequest);

        // Send to the OHTTP gateway.
        string serverEndpoint = this.configuration["OHTTP_GATEWAY_ENDPOINT"]
            ?? throw new InvalidOperationException(
                "OHTTP_GATEWAY_ENDPOINT environment variable is not set.");

        string gatewayUrl = $"{serverEndpoint}{OhttpConstants.GatewayPathPrefix}{modelName}";
        this.logger.LogInformation("Sending OHTTP request to {Url}.", gatewayUrl);

        HttpClient client = this.httpClientFactory.CreateClient("OhttpGateway");
        using var gatewayRequest = new HttpRequestMessage(HttpMethod.Post, gatewayUrl);
        gatewayRequest.Content = new ByteArrayContent(ohttpRequest);
        gatewayRequest.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(
                OhttpConstants.OhttpRequestMediaType);

        // Forward the authorization header so the gateway's auth check passes.
        if (this.Request.Headers.TryGetValue(
            "x-ms-cleanroom-authorization", out var authValue))
        {
            gatewayRequest.Headers.TryAddWithoutValidation(
                "x-ms-cleanroom-authorization", authValue.ToString());
        }

        HttpResponseMessage gatewayResponse;
        try
        {
            gatewayResponse = await client.SendAsync(
                gatewayRequest,
                HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to reach OHTTP gateway at {Url}.", gatewayUrl);

            // Invalidate key cache on connection errors.
            this.keyConfigCache.Invalidate();
            throw;
        }

        await gatewayResponse.ValidateStatusCodeAsync(this.logger);

        try
        {
            return await this.StreamChunkedResponse(gatewayResponse, encapsulator);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to decapsulate OHTTP response.");
            this.keyConfigCache.Invalidate();
            throw;
        }
    }

    /// <summary>
    /// Streams a chunked OHTTP response (message/ohttp-chunked-res) to the caller.
    /// Each encrypted chunk is decrypted and its inner Binary HTTP body data is
    /// written to the response stream immediately, enabling true end-to-end streaming.
    /// </summary>
    private async Task<IActionResult> StreamChunkedResponse(
        HttpResponseMessage gatewayResponse,
        OhttpEncapsulator encapsulator)
    {
        using Stream responseStream =
            await gatewayResponse.Content.ReadAsStreamAsync();

        // Delegate the entire decrypt-and-stream pipeline to ohttp-common.
        DecapsulateResult result =
            await ChunkedOhttpStream.DecapsulateAsync(
                responseStream,
                this.Response.Body,
                encapsulator,
                onHeadersReady: (status, headers) =>
                {
                    this.Response.StatusCode = status;
                    if (headers.TryGetValue("content-type", out string? ct))
                    {
                        this.Response.ContentType = ct;
                    }

                    return Task.CompletedTask;
                });

        this.logger.LogInformation(
            "Streaming chunked response, inner status {StatusCode}.",
            result.StatusCode);

        this.logger.LogInformation(
            "Finished streaming chunked OHTTP response ({Size} bytes).",
            result.TotalBodyBytes);

        return new EmptyResult();
    }
}
