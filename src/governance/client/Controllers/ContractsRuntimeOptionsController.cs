// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ContractsRuntimeOptionsController : ClientControllerBase
{
    public ContractsRuntimeOptionsController(
        ILogger<ContractsRuntimeOptionsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpPost("/contracts/{contractId}/execution/enable")]
    public async Task EnableExecution([FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.PostAsync($"app/contracts/{contractId}/enable", content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
    }

    [HttpPost("/contracts/{contractId}/execution/disable")]
    public async Task DisableExecution([FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.PostAsync($"app/contracts/{contractId}/disable", content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
    }

    [HttpPost("/contracts/{contractId}/logging/propose-enable")]
    public async Task<IActionResult> ProposeEnableLogging(
        [FromRoute] string contractId)
    {
        return await this.Propose(contractId, "set_contract_runtime_options_enable_logging");
    }

    [HttpPost("/contracts/{contractId}/logging/propose-disable")]
    public async Task<IActionResult> ProposeDisableLogging(
        [FromRoute] string contractId)
    {
        return await this.Propose(contractId, "set_contract_runtime_options_disable_logging");
    }

    [HttpPost("/contracts/{contractId}/telemetry/propose-enable")]
    public async Task<IActionResult> ProposeEnableTelemetry(
        [FromRoute] string contractId)
    {
        return await this.Propose(contractId, "set_contract_runtime_options_enable_telemetry");
    }

    [HttpPost("/contracts/{contractId}/telemetry/propose-disable")]
    public async Task<IActionResult> ProposeDisableTelemetry(
        [FromRoute] string contractId)
    {
        return await this.Propose(contractId, "set_contract_runtime_options_disable_telemetry");
    }

    [HttpPost("/contracts/{contractId}/checkstatus/execution")]
    public async Task<JsonObject> ExecutionStatus(
        [FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/contracts/{contractId}/checkstatus/execution",
            content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/contracts/{contractId}/checkstatus/logging")]
    public async Task<JsonObject> LoggingStatus(
        [FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/contracts/{contractId}/checkstatus/logging",
            content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPost("/contracts/{contractId}/checkstatus/telemetry")]
    public async Task<JsonObject> TelemetryStatus(
    [FromRoute] string contractId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await appClient.PostAsync(
            $"app/contracts/{contractId}/checkstatus/telemetry",
            content: null);
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    private async Task<IActionResult> Propose(
        string contractId,
        string actionName)
    {
        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = actionName,
                        ["args"] = new JsonObject
                        {
                            ["contractId"] = contractId
                        }
                    }
                }
        };

        var ccfClient = await this.CcfClientManager.GetGovClient();
        var coseSignKey = this.CcfClientManager.GetCoseSignKey();
        var payload =
            await GovernanceCose.CreateGovCoseSign1Message(
                coseSignKey,
                GovMessageType.Proposal,
                proposalContent.ToJsonString());
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals:create" +
            $"?api-version={this.CcfClientManager.GetGovApiVersion()}",
            payload);
        using HttpResponseMessage response = await ccfClient.SendAsync(request);
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.Logger);
        await response.WaitGovTransactionCommittedAsync(this.Logger, this.CcfClientManager);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return this.Ok(jsonResponse!);
    }
}
