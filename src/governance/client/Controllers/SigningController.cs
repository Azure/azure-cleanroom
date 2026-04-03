// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class SigningController : ClientControllerBase
{
    public SigningController(
        ILogger<SigningController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpPost("/signing/generateSigningKey")]
    public async Task<JsonObject> GenerateSigningKey()
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"app/signing/generateSigningKey"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
            this.Response.StatusCode = (int)response.StatusCode;
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }

    [HttpPost("/signing/info")]
    public async Task<JsonObject> GetSigningInfo()
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request = new(HttpMethod.Post, $"app/signing/info"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }
}
