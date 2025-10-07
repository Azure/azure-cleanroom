// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UsersController : ClientControllerBase
{
    public UsersController(
        ILogger<UsersController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/users")]
    public async Task<JsonObject> GetUsers()
    {
        var ccfClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await ccfClient.GetAsync("app/users/identities");
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpGet("/users/{userId}")]
    public async Task<JsonObject> GetUserById([FromRoute] string userId)
    {
        var ccfClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await ccfClient.GetAsync($"app/users/identities/{userId}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }
}
