// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc;
using OhttpCommon;
using OhttpGateway;

namespace Controllers;

/// <summary>
/// OHTTP gateway endpoint. Decrypts an OHTTP-encapsulated request, proxies it
/// to the predictor pod, and re-encrypts the response.
/// </summary>
[ApiController]
public class GatewayController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly HpkeKeyManager keyManager;
    private readonly PredictorClientManager predictorClientManager;

    public GatewayController(
        ILogger logger,
        IConfiguration configuration,
        HpkeKeyManager keyManager,
        PredictorClientManager predictorClientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.keyManager = keyManager;
        this.predictorClientManager = predictorClientManager;
    }

    /// <summary>
    /// Receives an OHTTP request, decapsulates it, forwards the inner HTTP request
    /// to the model predictor, and returns a chunked OHTTP-encapsulated streaming response.
    /// </summary>
    /// <param name="modelName">The name of the model to proxy to.</param>
    /// <returns>The chunked OHTTP-encapsulated response from the predictor.</returns>
    [HttpPost("/ohttp-gateway/gateway/{modelName}")]
    public async Task<IActionResult> Gateway(string modelName)
    {
        // Validate content type.
        string? contentType = this.Request.ContentType;
        if (contentType == null ||
            !contentType.StartsWith(
                OhttpConstants.OhttpRequestMediaType, StringComparison.OrdinalIgnoreCase))
        {
            return this.BadRequest(
                $"Expected Content-Type: {OhttpConstants.OhttpRequestMediaType}.");
        }

        // Read the OHTTP request body.
        using var ms = new MemoryStream();
        await this.Request.Body.CopyToAsync(ms);
        byte[] ohttpRequestBytes = ms.ToArray();

        this.logger.LogInformation(
            "Received OHTTP request for model '{ModelName}' ({Size} bytes).",
            modelName,
            ohttpRequestBytes.Length);

        // Decapsulate.
        OhttpDecapsulator decapsulator = this.keyManager.CreateDecapsulator();
        byte[] binaryHttpRequest;
        try
        {
            binaryHttpRequest = decapsulator.DecapsulateRequest(ohttpRequestBytes);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Failed to decapsulate OHTTP request.");
            return this.BadRequest("Failed to decapsulate OHTTP request.");
        }

        // Parse the inner Binary HTTP request.
        BinaryHttpRequest innerRequest = BinaryHttp.DeserializeRequest(binaryHttpRequest);
        this.logger.LogInformation(
            "Inner request: {Method} {Path}",
            innerRequest.Method,
            innerRequest.Path);

        // Build the predictor URL.
        string predictorHost = this.GetPredictorHost(modelName);
        string predictorUrl = $"https://{predictorHost}{innerRequest.Path}";
        this.logger.LogInformation("Proxying to predictor: {Url}", predictorUrl);

        // Forward to predictor with streaming enabled.
        HttpClient client =
            await this.predictorClientManager.GetClient();
        using var proxyRequest = new HttpRequestMessage(
            new HttpMethod(innerRequest.Method),
            predictorUrl);

        // Copy inner headers to the proxy request.
        foreach (KeyValuePair<string, string> header in innerRequest.Headers)
        {
            // Skip pseudo-headers and host.
            if (header.Key.StartsWith(':') ||
                header.Key.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (innerRequest.Body.Length > 0)
        {
            proxyRequest.Content = new ByteArrayContent(innerRequest.Body);
            if (innerRequest.Headers.TryGetValue("content-type", out string? ct))
            {
                proxyRequest.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(ct);
            }
        }

        HttpResponseMessage predictorResponse;
        try
        {
            predictorResponse = await client.SendAsync(
                proxyRequest,
                HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to proxy request to predictor at {Url}.",
                predictorUrl);
            throw;
        }

        using (predictorResponse)
        {
            return await this.StreamChunkedResponse(
                predictorResponse,
                decapsulator);
        }
    }

    private async Task<IActionResult> StreamChunkedResponse(
        HttpResponseMessage predictorResponse,
        OhttpDecapsulator decapsulator)
    {
        // Collect predictor response headers.
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (predictorResponse.Content.Headers.ContentType != null)
        {
            responseHeaders["content-type"] =
                predictorResponse.Content.Headers.ContentType.ToString();
        }

        this.logger.LogInformation(
            "Streaming chunked OHTTP response, predictor status {StatusCode}.",
            (int)predictorResponse.StatusCode);

        // Create chunked response encryptor.
        ChunkedResponseEncryptor encryptor = decapsulator.CreateChunkedResponseEncryptor();

        // Begin writing the chunked OHTTP response.
        this.Response.ContentType = OhttpConstants.OhttpChunkedResponseMediaType;
        this.Response.StatusCode = 200;

        // Delegate the entire encrypt-and-stream pipeline to ohttp-common.
        using Stream predictorStream =
            await predictorResponse.Content.ReadAsStreamAsync();
        await ChunkedOhttpStream.EncapsulateAsync(
            predictorStream,
            this.Response.Body,
            encryptor,
            (int)predictorResponse.StatusCode,
            responseHeaders);

        return new EmptyResult();
    }

    private string GetPredictorHost(string modelName)
    {
        // Allow a blanket override for testing (e.g. docker-compose pointing at a mock).
        string? overrideHost = this.configuration["PREDICTOR_HOST_OVERRIDE"];
        if (!string.IsNullOrEmpty(overrideHost))
        {
            return overrideHost;
        }

        string ns = this.configuration["INFERENCING_NAMESPACE"]
            ?? "kserve-inferencing";
        return $"{modelName}-predictor-https.{ns}.svc";
    }
}
