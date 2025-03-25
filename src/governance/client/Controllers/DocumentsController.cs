// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class DocumentsController : ClientControllerBase
{
    private const string VersionKey = "version";
    private const string ProposalIdKey = "proposalId";
    private const string StateKey = "state";

    public DocumentsController(
        ILogger<DocumentsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/documents/{documentId}")]
    public async Task<JsonObject> GetDocument([FromRoute] string documentId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.GetAsync($"app/documents/{documentId}");
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpGet("/documents")]
    public async Task<JsonArray> ListDocuments()
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await appClient.GetAsync($"app/documents");
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonArray>();
        return jsonResponse!;
    }

    [HttpPut("/documents/{documentId}")]
    public async Task PutDocument([FromRoute] string documentId, [FromBody] JsonObject content)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request = new(HttpMethod.Put, $"app/documents/{documentId}"))
        {
            request.Content = new StringContent(
                content.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
        }
    }

    [HttpPost("/documents/{documentId}/propose")]
    public async Task<IActionResult> CreateProposal(
        [FromRoute] string documentId,
        [FromBody] JsonObject content)
    {
        var incomingVersion = content[VersionKey];
        if (incomingVersion == null)
        {
            return this.BadRequest(new ODataError(
                code: "VersionMissing",
                message: "Musty specify a Version value."));
        }

        JsonObject currentDocument;
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpResponseMessage gr =
            await appClient.GetAsync($"app/documents/{documentId}"))
        {
            await gr.ValidateStatusCodeAsync(this.Logger);
            currentDocument = (await gr.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        var currentVersion = currentDocument[VersionKey];
        if (currentVersion?.ToString() != incomingVersion.ToString())
        {
            return this.BadRequest(new ODataError(
                code: "DocumentModified",
                message: "The version value specified in the input does not match the current" +
                    " version value."));
        }

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "set_document",
                        ["args"] = new JsonObject
                        {
                            ["documentId"] = documentId,
                            ["document"] = currentDocument
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

    [HttpPost("/documents/{documentId}/vote_accept")]
    public Task<IActionResult> VoteAccept(
        [FromRoute] string documentId,
        [FromBody] JsonObject content)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "export function vote (proposal, proposerId) { return true }"
        };
        return this.Vote(documentId, content, ballot);
    }

    [HttpPost("/documents/{documentId}/vote_reject")]
    public Task<IActionResult> VoteReject(
        [FromRoute] string documentId,
        [FromBody] JsonObject content)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "export function vote (proposal, proposerId) { return false }"
        };
        return this.Vote(documentId, content, ballot);
    }

    [HttpPost("/documents/{documentId}/vote")]
    public async Task<IActionResult> Vote(
        [FromRoute] string documentId,
        [FromBody] JsonObject content)
    {
        var ballotNode = content["ballot"];
        if (ballotNode == null)
        {
            return this.BadRequest(new ODataError(
                code: "BallotKeyMissing",
                message: "Musty specify the ballot to vote with."));
        }

        JsonObject ballot = ballotNode.AsObject();
        return await this.Vote(documentId, content, ballot);
    }

    private async Task<IActionResult> Vote(
        string documentId,
        JsonObject content,
        JsonObject ballot)
    {
        var proposalId = content[ProposalIdKey];
        if (proposalId == null)
        {
            return this.BadRequest(new ODataError(
                code: "ProposalIdMissing",
                message: "Musty specify the proposalId to vote on."));
        }

        JsonObject currentDocument;
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpResponseMessage gr =
            await appClient.GetAsync($"app/documents/{documentId}"))
        {
            await gr.ValidateStatusCodeAsync(this.Logger);
            currentDocument = (await gr.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        var currentProposalId = currentDocument[ProposalIdKey];
        if (currentProposalId == null || string.IsNullOrEmpty(currentProposalId.ToString()))
        {
            return this.BadRequest(new ODataError(
                code: "DocumentNotProposed",
                message: $"Cannot vote on the document as it is currently not proposed. " +
                    $"Document state is: {currentDocument[StateKey]?.ToString()}"));
        }

        if (currentProposalId.ToString() != proposalId.ToString())
        {
            return this.BadRequest(new ODataError(
                code: "ProposalIdMismatch",
                message: $"The proposalId '{proposalId}' specified in the input " +
                    $"does not match the current proposalId '{currentProposalId}' value."));
        }

        var ccfClient = await this.CcfClientManager.GetGovClient();
        var coseSignKey = this.CcfClientManager.GetCoseSignKey();
        var payload = await GovernanceCose.CreateGovCoseSign1Message(
            coseSignKey,
            GovMessageType.Ballot,
            ballot.ToJsonString(),
            proposalId.ToString());
        using HttpRequestMessage request = Cose.CreateHttpRequestMessage(
            $"gov/members/proposals/{proposalId}/ballots/" +
            $"{this.CcfClientManager.GetMemberId()}:submit" +
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
