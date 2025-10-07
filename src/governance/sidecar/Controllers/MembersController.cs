// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class MembersController : ControllerBase
{
    private readonly ILogger<MembersController> logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly Routes routes;

    public MembersController(
        ILogger<MembersController> logger,
        CcfClientManager ccfClientManager,
        Routes routes)
    {
        this.logger = logger;
        this.ccfClientManager = ccfClientManager;
        this.routes = routes;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpPost("/members")]
    public async Task<JsonObject> GetMembers()
    {
        var appClient = await this.ccfClientManager.GetAppClient();
        var url = $"gov/service/members?api-version=2024-07-01";
        var members = (await appClient.GetFromJsonAsync<JsonObject>(url))!;
        return members!;
    }
}
