// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UsersController : ControllerBase
{
    private readonly ILogger<ConsentCheckController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public UsersController(
        ILogger<ConsentCheckController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/users/isactive")]
    public async Task<JsonObject> IsActiveUser()
    {
        var url = this.routes.IsActiveUser(this.WebContext);
        var appClient = await this.ccfClientManager.GetAppClient();

        using HttpRequestMessage request = new(HttpMethod.Post, url);

        // Copy over the authorization header which should contain the user token that gets used
        // to identify the user.
        string? authHeader = this.Request.Headers.Authorization;
        if (authHeader != null)
        {
            var parts = authHeader.Split(' ', 2);
            if (parts.Length != 2)
            {
                throw new Exception(
                    $"Expecting Authorization header to have 2 parts but found {parts.Length}");
            }

            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(parts[0], parts[1]);
        }
        else
        {
            throw new Exception($"Expecting Authorization header to be present.");
        }

        using HttpResponseMessage response = await appClient.SendAsync(request);
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.logger);
        var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return jsonResponse!;
    }
}
