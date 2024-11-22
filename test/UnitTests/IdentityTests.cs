// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace UnitTests;

/// <summary>
/// Tests for identity sidecar.
/// </summary>
[TestClass]
public class IdentityTests : UnitTestBase
{
    /// <summary>
    /// Test identity with a managed identity.
    /// </summary>
    /// <returns>A task representing the operation.</returns>
    [TestMethod]
    [TestCategory("identity")]
    public async Task TestIdentityWithManagedIdentity()
    {
        AccessToken token = await this.GetAccessTokenFromIdentityAsync();
        await this.ValidateTokenUsingSecureKeyReleaseAsync(token);
    }

    /// <summary>
    /// Test identity with invalid client ID.
    /// </summary>
    /// <returns>A task representing the operation.</returns>
    [TestMethod]
    [TestCategory("identity")]
    public async Task TestIdentityWithInvalidClientId()
    {
        string identityPort = this.Configuration[TestSettingName.IdentityPort]!;
        string tenantId = this.Configuration[TestSettingName.TenantId]!;

        using HttpClient client = new();
        client.BaseAddress = new Uri($"http://localhost:{identityPort}");

        UriBuilder builder = new(client.BaseAddress)
        {
            Path = "/metadata/identity/oauth2/token",
            Query = $"scope=https://vault.azure.net/.default&clientId=doesnotmatter&" +
                $"tenantId={tenantId}",
        };

        var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);

        var responseStr = await response.Content.ReadAsStringAsync();

        JsonNode jsonNode = JsonNode.Parse(responseStr)!;
        Assert.IsNotNull(jsonNode);
        Assert.AreEqual("InvalidClientId", jsonNode["code"]!.ToString());
    }

    /// <summary>
    /// Helper method to fetch an <see cref="AccessToken"/> from the identity sidecar.
    /// </summary>
    /// <returns>A task representing the operation.</returns>
    private async Task<AccessToken> GetAccessTokenFromIdentityAsync()
    {
        string identityPort = this.Configuration[TestSettingName.IdentityPort]!;
        string tenantId = this.Configuration[TestSettingName.TenantId]!;

        using HttpClient client = new();
        client.BaseAddress = new Uri($"http://localhost:{identityPort}");

        UriBuilder builder = new(client.BaseAddress)
        {
            Path = "/metadata/identity/oauth2/token",
            Query = $"scope=https://vault.azure.net/.default&tenantId={tenantId}",
        };

        var request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);

        HttpResponseMessage response = await client.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.Logger);

        var responseStr = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<AccessToken>(responseStr);
    }

    /// <summary>
    /// Helper method to validate an access token by using it for SKR.
    /// </summary>
    /// <param name="token">The access token.</param>
    /// <returns>A task representing the operation.</returns>
    private async Task ValidateTokenUsingSecureKeyReleaseAsync(AccessToken token)
    {
        string akvEndpoint = this.Configuration[TestSettingName.AkvEndpoint]!;
        string maaEndpoint = this.Configuration[TestSettingName.MaaEndpoint]!;
        string kid = this.Configuration[TestSettingName.Kid]!;
        string skrPort = this.Configuration[TestSettingName.SkrPort]!;

        var payload = new SecureKeyReleasePayload
        {
            MaaEndpoint = maaEndpoint,
            AkvEndpoint = akvEndpoint,
            KID = kid,
            AccessToken = token.Token,
        };

        await Utils.SecureKeyReleaseAsync(skrPort, payload, this.Logger);
    }
}
