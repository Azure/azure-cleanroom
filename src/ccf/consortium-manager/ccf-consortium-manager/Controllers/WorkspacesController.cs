// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcfConsortiumMgr.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CcfConsortiumMgr;

[AllowAnonymous]
[ApiController]
public class WorkspacesController : ControllerBase
{
    private readonly ILogger logger;
    private readonly CcfConsortiumManager ccfConsortiumManager;

    public WorkspacesController(
        ILogger logger,
        CcfConsortiumManager ccfConsortiumManager)
    {
        this.logger = logger;
        this.ccfConsortiumManager = ccfConsortiumManager;
    }

    [HttpGet("/ready")]
    public IActionResult Ready()
    {
        return this.Ok(new JsonObject
        {
            ["status"] = "up"
        });
    }

    [HttpGet("/show")]
    public IActionResult Show()
    {
        var wsConfig =
            new WorkspaceConfiguration()
            {
                EnvironmentVariables = Environment.GetEnvironmentVariables()
            };
        return this.Ok(wsConfig);
    }

    [HttpGet("/report")]
    public async Task<IActionResult> GetReport()
    {
        ConsortiumManagerReport report = await this.ccfConsortiumManager.GetReport();
        return this.Ok(report);
    }

    [HttpPost("/generateKeys")]
    public async Task<IActionResult> GenerateKeys()
    {
        await this.ccfConsortiumManager.GenerateKeys();
        return this.Ok();
    }
}
