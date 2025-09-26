// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CcrSecrets;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class SecretsController : ControllerBase
{
    private readonly IConfiguration config;
    private readonly ILogger<SecretsController> logger;
    private readonly SecretsClient skrClient;

    public SecretsController(
        IConfiguration config,
        ILogger<SecretsController> logger)
    {
        this.config = config;
        this.logger = logger;
        this.skrClient = new SecretsClient(logger, config);
    }

    [HttpPost("/secrets/unwrap")]
    public async Task<IActionResult> UnwrapSecret([FromBody] UnwrapSecretRequest unwrapRequest)
    {
        if (string.IsNullOrEmpty(this.config[SettingName.IdentityPort]))
        {
            return this.BadRequest(new ODataError(
                code: "IdentityPortNotSet",
                message: "IDENTITY_PORT environment variable not set."));
        }

        if (string.IsNullOrEmpty(this.config[SettingName.SkrPort]))
        {
            return this.BadRequest(new ODataError(
                code: "SkrPortNotSet",
                message: "SKR_PORT environment variable not set."));
        }

        byte[] plainText = await this.skrClient.UnwrapSecret(unwrapRequest);
        return this.Ok(new JsonObject
        {
            ["value"] = Convert.ToBase64String(plainText)
        });
    }
}
