// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using CcfProvider;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class WorkspacesController : ControllerBase
{
    private readonly ILogger logger;
    private readonly CcfClientManager ccfClientManager;
    private readonly RecoveryAgentClientManager agentClientManager;

    public WorkspacesController(
        CcfClientManager ccfClientManager,
        RecoveryAgentClientManager agentClientManager,
        ILogger logger)
    {
        this.ccfClientManager = ccfClientManager;
        this.agentClientManager = agentClientManager;
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

    [HttpPost("/configure")]
    public async Task<IActionResult> SetWorkspaceConfig(
        [FromForm] WorkspaceConfigurationModel model)
    {
        if (model.SigningCertPemFile == null && string.IsNullOrEmpty(model.SigningCertId))
        {
            return this.BadRequest("Either SigningCertPemFile or SigningCertId must be specified");
        }

        if (model.SigningCertPemFile != null && !string.IsNullOrEmpty(model.SigningCertId))
        {
            return this.BadRequest(
                "Only one of SigningCertPemFile or SigningCertId must be specified");
        }

        CoseSignKey coseSignKey;
        if (model.SigningCertPemFile != null)
        {
            if (model.SigningCertPemFile.Length <= 0)
            {
                return this.BadRequest("No signing cert file was uploaded.");
            }

            if (model.SigningKeyPemFile == null || model.SigningKeyPemFile.Length <= 0)
            {
                return this.BadRequest("No signing key file was uploaded.");
            }

            string signingCert;
            using var reader = new StreamReader(model.SigningCertPemFile.OpenReadStream());
            signingCert = await reader.ReadToEndAsync();

            string signingKey;
            using var reader2 = new StreamReader(model.SigningKeyPemFile.OpenReadStream());
            signingKey = await reader2.ReadToEndAsync();

            coseSignKey = new CoseSignKey(signingCert, signingKey);
        }
        else
        {
            Uri signingCertId;
            try
            {
                signingCertId = new Uri(model.SigningCertId!);
            }
            catch (Exception e)
            {
                return this.BadRequest($"Invalid signingKid value: {e.Message}.");
            }

            var creds = new DefaultAzureCredential();
            coseSignKey = await CoseSignKey.FromKeyVault(signingCertId, creds);
        }

        this.ccfClientManager.SetSigningConfig(new SigningConfiguration
        {
            CoseSignKey = coseSignKey
        });
        this.agentClientManager.SetSigningConfig(new SigningConfiguration
        {
            CoseSignKey = coseSignKey
        });
        return this.Ok("Workspace details configured successfully.");
    }

    [HttpGet("/show")]
    public IActionResult Show([FromQuery] bool? signingKey = false)
    {
        WorkspaceConfiguration copy;
        var wsConfig = this.ccfClientManager.TryGetSigningConfig();
        if (wsConfig != null)
        {
            copy = JsonSerializer.Deserialize<WorkspaceConfiguration>(
                JsonSerializer.Serialize(wsConfig))!;
            if (!signingKey.GetValueOrDefault())
            {
                copy.SigningKey = "<redacted>";
            }
        }
        else
        {
            copy = new WorkspaceConfiguration();
        }

        copy.EnvironmentVariables = Environment.GetEnvironmentVariables();
        return this.Ok(copy);
    }
}
