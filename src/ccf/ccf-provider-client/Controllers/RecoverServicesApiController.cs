// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using CcfProvider;
using CcfProviderClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class RecoverServicesApiController : CCfClientController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly RecoveryServiceClientManager serviceClientManager;

    public RecoverServicesApiController(
        ILogger logger,
        IConfiguration configuration,
        RecoveryServiceClientManager serviceClientManager,
        ProvidersRegistry providers)
        : base(logger, configuration, providers)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.serviceClientManager = serviceClientManager;
    }

    [HttpGet("/recoveryServices/api/members")]
    public async Task<IActionResult> GetMembers(
        [FromBody] RecoveryServiceApiInput content)
    {
        var error = ValidateGetInput();
        if (error != null)
        {
            return error;
        }

        var svcClient = await this.serviceClientManager.GetClient(content.RecoveryService);
        var response = await svcClient.GetFromJsonAsync<JsonArray>("/members");
        return this.Ok(response);

        IActionResult? ValidateGetInput()
        {
            return this.ValidateInput(content.RecoveryService);
        }
    }

    [HttpGet("/recoveryServices/api/members/{memberName}")]
    public async Task<IActionResult> GetMembers(
        [FromRoute] string memberName,
        [FromBody] RecoveryServiceApiInput content)
    {
        var error = ValidateGetInput();
        if (error != null)
        {
            return error;
        }

        var svcClient = await this.serviceClientManager.GetClient(content.RecoveryService);
        var response = await svcClient.GetFromJsonAsync<JsonObject>($"/members/{memberName}");
        return this.Ok(response);

        IActionResult? ValidateGetInput()
        {
            return this.ValidateInput(content.RecoveryService);
        }
    }

    [HttpGet("/recoveryServices/api/report")]
    public async Task<IActionResult> GetReport(
        [FromBody] RecoveryServiceApiInput content,
        [FromQuery] bool? skipVerify = false)
    {
        var error = ValidateGetInput();
        if (error != null)
        {
            return error;
        }

        var svcClient = await this.serviceClientManager.GetClient(content.RecoveryService);
        var response = await svcClient.GetFromJsonAsync<JsonObject>("/report");
        response!["verified"] = false;
        if (!skipVerify.GetValueOrDefault())
        {
            var report = response?["report"];
            {
                if (report != null)
                {
                    var attestationReport = JsonSerializer.Deserialize<AttestationReport>(report)!;
                    SnpReport.VerifySnpAttestation(
                        attestationReport.Attestation,
                        attestationReport.PlatformCertificates,
                        attestationReport.UvmEndorsements);
                    response!["verified"] = true;
                }
            }
        }

        return this.Ok(response);

        IActionResult? ValidateGetInput()
        {
            return this.ValidateInput(content.RecoveryService);
        }
    }

    [HttpGet("/recoveryServices/api/network/joinpolicy")]
    public async Task<IActionResult> GetNetworkJoinPolicy(
        [FromBody] RecoveryServiceApiInput content)
    {
        var error = ValidateGetInput();
        if (error != null)
        {
            return error;
        }

        var svcClient = await this.serviceClientManager.GetClient(content.RecoveryService);
        var response = await svcClient.GetFromJsonAsync<JsonObject>("/network/joinpolicy");
        return this.Ok(response);

        IActionResult? ValidateGetInput()
        {
            return this.ValidateInput(content.RecoveryService);
        }
    }

    private IActionResult? ValidateInput(RecoveryServiceConfig? recoveryService)
    {
        if (recoveryService == null)
        {
            return this.BadRequest(new ODataError(
                code: "InputMissing",
                message: "recoveryService must be specified."));
        }

        if (string.IsNullOrEmpty(recoveryService.Endpoint))
        {
            return this.BadRequest(new ODataError(
                code: "InputMissing",
                message: "recoveryService.endpoint must be specified."));
        }

        if (string.IsNullOrEmpty(recoveryService.ServiceCert))
        {
            return this.BadRequest(new ODataError(
                code: "InputMissing",
                message: "recoveryService.serviceCert must be specified."));
        }

        return null;
    }
}
