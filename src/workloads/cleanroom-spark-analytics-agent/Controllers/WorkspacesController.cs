// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class WorkspacesController : ControllerBase
{
    private readonly ILogger logger;

    public WorkspacesController(ILogger logger)
    {
        this.logger = logger;
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
        WorkspaceConfiguration copy = new();
        copy.EnvironmentVariables = Environment.GetEnvironmentVariables();
        return this.Ok(copy);
    }
}
