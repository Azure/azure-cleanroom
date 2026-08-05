// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Identity.Configuration;
using Identity.CredentialManager;
using Microsoft.AspNetCore.Mvc;

namespace IdentitySidecar.Controllers;

[ApiController]
public class IdentityController : ControllerBase
{
    private readonly ILogger<IdentityController> logger;
    private readonly CredentialManager credentialManager;

    public IdentityController(
        ILogger<IdentityController> logger,
        CredentialManager credentialManager)
    {
        this.logger = logger;
        this.credentialManager = credentialManager;
    }

    [HttpGet("/metadata/identity/oauth2/token")]
    public async Task<AccessToken> GetToken(
        [FromQuery] string scope,
        [FromQuery] string tenantId,
        [FromQuery] string? clientId,
        [FromQuery] string? apiVersion)
    {
        clientId = clientId ?? string.Empty;

        this.logger.LogInformation(
            $"Fetching access token for scope '{scope}' using client ID '{clientId}'. " +
            $"Tenant ID: '{tenantId}'.");

        return await this.credentialManager.GetCredential(clientId).GetTokenAsync(
            scope,
            tenantId);
    }

    [HttpGet("/metadata/identity/{tenantId}/{clientId}/oauth2/token")]
    public async Task<MSITokenResponse> GetMsiToken(
        [FromRoute] string tenantId,
        [FromRoute] string? clientId,
        [FromQuery] string resource,
        [FromQuery] string? apiVersion)
    {
        clientId = clientId ?? string.Empty;

        var scope = resource + "/.default";
        this.logger.LogInformation(
            $"Fetching access token for scope '{scope}' using client ID '{clientId}'. " +
            $"Tenant ID: '{tenantId}'.");
        var accessToken = await this.credentialManager.GetCredential(clientId).GetTokenAsync(
            scope,
            tenantId);

        return new MSITokenResponse
        {
            AccessToken = accessToken.Token,
            ExpiresOn = accessToken.ExpiresOn.ToUnixTimeSeconds(),
            ExpiresIn = accessToken.ExpiresOn.ToUnixTimeSeconds() -
                DateTimeOffset.Now.ToUnixTimeSeconds(),
            RefreshToken = string.Empty
        };
    }

    /// <summary>
    /// The MSI authentication flow is handled by the Azure identity client module, which
    /// can be found in the Azure SDK for Go.The Azure identity client attempts to reach
    /// the endpoint specified in the MSI_ENDPOINT environment variable. The cloud shell
    /// flow is used for this purpose. You can refer to the code snippet here:
    /// https://github.com/Azure/azure-sdk-for-go/blob/main/sdk/azidentity/managed_identity_client.go#L478.
    /// </summary>
    /// <param name="tenantId">tenant id.</param>
    /// <param name="clientId">client id.</param>
    /// <returns>MSI token.</returns>
    [HttpPost("/metadata/identity/{tenantId}/{clientId}/oauth2/token")]
    public async Task<MSITokenResponse> GetMsiToken(
        [FromRoute] string tenantId,
        [FromRoute] string? clientId)
    {
        clientId = clientId ?? string.Empty;
        var formData = await this.Request.ReadFormAsync();
        var resource = formData["resource"];
        var scope = resource + "/.default";
        this.logger.LogInformation(
            $"Fetching access token for scope '{scope}' using client ID '{clientId}'. " +
            $"Tenant ID: '{tenantId}'.");
        var accessToken = await this.credentialManager.GetCredential(clientId).GetTokenAsync(
            scope,
            tenantId);

        return new MSITokenResponse
        {
            AccessToken = accessToken.Token,
            ExpiresOn = accessToken.ExpiresOn.ToUnixTimeSeconds(),
            ExpiresIn = accessToken.ExpiresOn.ToUnixTimeSeconds() -
                DateTimeOffset.Now.ToUnixTimeSeconds(),
            RefreshToken = string.Empty
        };
    }

    [HttpPost("/metadata/identity/register")]
    public async Task<IActionResult> RegisterIdentity()
    {
        using var reader = new StreamReader(this.Request.Body);
        var body = await reader.ReadToEndAsync();
        var identity = JsonSerializer.Deserialize<ApplicationIdentity>(
            body,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (identity == null || string.IsNullOrEmpty(identity.ClientId))
        {
            return this.BadRequest("Identity with valid ClientId is required.");
        }

        if (identity.Credential == null)
        {
            return this.BadRequest(
                "Credential configuration is required.");
        }

        this.logger.LogInformation(
            $"Registering identity for client ID: " +
            $"'{identity.ClientId}'.");

        bool added =
            this.credentialManager.RegisterCredential(identity);
        if (added)
        {
            this.logger.LogInformation(
                $"Successfully registered identity for " +
                $"client ID: '{identity.ClientId}'.");
        }
        else
        {
            this.logger.LogInformation(
                $"Identity already registered for " +
                $"client ID: '{identity.ClientId}'.");
        }

        return this.Ok(
            new
            {
                clientId = identity.ClientId,
                registered = added
            });
    }

    // MSITokenResponse represents the expected response type from managed identity. Please
    // find more details at:
    // https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/how-to-use-vm-token#get-a-token-using-http.
    public class MSITokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = default!;

        [JsonPropertyName("refresh_token")]
        public string RefreshToken { get; set; } = default!;

        [JsonPropertyName("expires_in")]
        public long ExpiresIn { get; set; } = default!;

        [JsonPropertyName("expires_on")]
        public long ExpiresOn { get; set; } = default!;
    }
}