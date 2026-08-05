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

    /// <summary>
    /// Health check endpoint.
    /// </summary>
    /// <returns>A status object indicating the client is up.</returns>
    [HttpGet("/ready")]
    public IActionResult Ready()
    {
        return this.Ok(new JsonObject
        {
            ["status"] = "up"
        });
    }
}
