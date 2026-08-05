// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

/// <summary>
/// Handles ext_authz requests from Envoy. Validates caller authorization and extracts the
/// model name from the request path or body, returning it as a response header for Envoy
/// to use in dynamic routing to the predictor pod.
/// </summary>
[ApiController]
public class AuthzController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly ActiveUserChecker activeUserChecker;

    public AuthzController(
        ILogger logger,
        IConfiguration configuration,
        ActiveUserChecker activeUserChecker)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.activeUserChecker = activeUserChecker;
    }

    /// <summary>
    /// Envoy ext_authz endpoint for OHTTP gateway routes. Validates the caller only — no
    /// model name extraction or routing headers are needed because the OHTTP gateway handles
    /// its own request routing after decapsulation.
    /// </summary>
    /// <param name="path">The request path forwarded by Envoy after the /authz prefix.</param>
    /// <returns>200 on success, 401 on auth failure.</returns>
    [HttpPost("/authz/ohttp-gateway/{**path}")]
    [HttpGet("/authz/ohttp-gateway/{**path}")]
    public async Task<IActionResult> CheckAuthOhttp([FromRoute] string path)
    {
        // Key discovery and attestation endpoints are public — clients need the HPKE
        // public key before they can construct an authenticated OHTTP request.
        if (path.Equals(".well-known/ohttp-keys", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("ohttp-keys/attestation", StringComparison.OrdinalIgnoreCase))
        {
            this.logger.LogInformation(
                "Skipping auth check for public OHTTP endpoint '{Path}'.",
                path);
            return this.Ok();
        }

        try
        {
            await this.activeUserChecker.CheckActive(this.Request, useCache: true);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, $"OHTTP authorization check failed for {path}.");
            return this.StatusCode(
                401,
                new ODataError(
                    code: "Unauthorized",
                    message: ex.Message));
        }

        this.logger.LogInformation(
            "OHTTP authorization succeeded for path '{Path}'.",
            path);
        return this.Ok();
    }

    /// <summary>
    /// Envoy ext_authz endpoint. Validates the caller and extracts the model name.
    /// Returns 200 with x-model-name header on success, 401 on auth failure.
    /// </summary>
    /// <param name="path">The request path forwarded by Envoy after the /authz prefix.</param>
    /// <returns>200 with x-model-name header on success, 401 or 400 on failure.</returns>
    [HttpPost("/authz/{**path}")]
    [HttpGet("/authz/{**path}")]
    public async Task<IActionResult> CheckAuth([FromRoute] string path)
    {
        try
        {
            await this.activeUserChecker.CheckActive(this.Request, useCache: true);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, $"Authorization check failed for {path}.");
            return this.StatusCode(
                401,
                new ODataError(
                    code: "Unauthorized",
                    message: ex.Message));
        }

        string? modelName = await this.ExtractModelName(path);
        if (string.IsNullOrEmpty(modelName))
        {
            this.logger.LogError(
                "Could not extract model name from path '{Path}'.",
                path);
            return this.BadRequest("Could not determine model name.");
        }

        string predictorNamespace =
            this.configuration["INFERENCING_NAMESPACE"] ?? "kserve-inferencing";
        string modelEndpoint =
            $"{modelName}-predictor-https.{predictorNamespace}.svc";

        this.logger.LogInformation(
            "Authorization succeeded for model '{ModelName}', endpoint '{Endpoint}', path '{Path}'.",
            modelName,
            modelEndpoint,
            path);

        // Return the model name and full predictor endpoint as response headers.
        // Envoy ext_authz forwards these to the upstream request. The Lua filter
        // uses x-model-endpoint as the Host header for dynamic forward proxy routing.
        this.Response.Headers["x-model-name"] = modelName;
        this.Response.Headers["x-model-endpoint"] = modelEndpoint;
        return this.Ok();
    }

    private async Task<string?> ExtractModelName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            this.logger.LogError("Request path is null or empty, cannot extract model name.");
            return null;
        }

        // V1: v1/models/{modelName}:predict or v1/models/{modelName}:explain
        // V2: v2/models/{modelName}/infer or v2/models/{modelName}/versions/...
        // KServe OpenAI: openai/v1/chat/completions, openai/v1/completions, etc.
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Check for v1/models/{name} or v2/models/{name} pattern.
        for (int i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("models", StringComparison.OrdinalIgnoreCase) &&
                i > 0 &&
                (segments[i - 1] == "v1" || segments[i - 1] == "v2"))
            {
                // The model name may contain ":predict" or ":explain" suffix.
                string modelSegment = segments[i + 1];
                int colonIndex = modelSegment.IndexOf(':');
                return colonIndex >= 0
                    ? modelSegment[..colonIndex]
                    : modelSegment;
            }
        }

        // OpenAI paths: v1/chat/completions, v1/completions, v1/embeddings.
        // Model name is in the JSON request body.
        return await this.ExtractModelNameFromBody();
    }

    private async Task<string?> ExtractModelNameFromBody()
    {
        if (!this.Request.Body.CanRead ||
            this.Request.ContentLength == null ||
            this.Request.ContentLength == 0)
        {
            this.logger.LogError(
                "Request body is not readable or empty, cannot extract model name.");
            return null;
        }

        try
        {
            // Enable buffering so the body can be read without consuming it.
            this.Request.EnableBuffering();
            this.Request.Body.Position = 0;
            using var reader = new StreamReader(
                this.Request.Body,
                leaveOpen: true);
            string body = await reader.ReadToEndAsync();
            this.Request.Body.Position = 0;

            var json = JsonNode.Parse(body);
            var value = json?["model"]?.GetValue<string>();
            if (string.IsNullOrEmpty(value))
            {
                this.logger.LogError(
                    "Model field is missing or empty in request body, cannot extract model name.");
            }

            return value;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to extract model name from request body.");
            return null;
        }
    }
}
