// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class NetworksController : ClientControllerBase
{
    public NetworksController(
        ILogger<NetworksController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/network/show")]
    public async Task<JsonObject> Get()
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/info?api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpGet("/node/ready/app")]
    public async Task<IActionResult> AppReady()
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage response = await ccfClient.GetAsync("node/ready/app");
        await response.ValidateStatusCodeAsync(this.Logger);
        return this.StatusCode((int)response.StatusCode);
    }

    [HttpGet("/node/ready/gov")]
    public async Task<IActionResult> GovReady()
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage response = await ccfClient.GetAsync("node/ready/gov");
        await response.ValidateStatusCodeAsync(this.Logger);
        return this.StatusCode((int)response.StatusCode);
    }
}
