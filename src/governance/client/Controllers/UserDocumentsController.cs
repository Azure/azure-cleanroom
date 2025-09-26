// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UserDocumentsController : UserProposalControllerBase
{
    private const string VersionKey = "version";
    private const string ProposalIdKey = "proposalId";
    private const string StateKey = "state";
    private const string ApproversKey = "approvers";

    public UserDocumentsController(
        ILogger<UserDocumentsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/userdocuments/{documentId}")]
    public async Task<JsonObject> GetUserDocument([FromRoute] string documentId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response =
            await appClient.GetAsync($"app/userdocuments/{documentId}");
        await response.ValidateStatusCodeAsync(this.Logger);
        this.Response.CopyHeaders(response.Headers);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpGet("/userdocuments")]
    public async Task<JsonObject> ListUserDocuments()
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using HttpResponseMessage response = await appClient.GetAsync($"app/userdocuments");
        this.Response.CopyHeaders(response.Headers);
        await response.ValidateStatusCodeAsync(this.Logger);
        var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        return jsonResponse!;
    }

    [HttpPut("/userdocuments/{documentId}")]
    public async Task PutUserDocument(
        [FromRoute] string documentId,
        [FromBody] JsonObject content)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Put, $"app/userdocuments/{documentId}"))
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

    [HttpPost("/userdocuments/{documentId}/propose")]
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

        JsonObject currentUserDocument;
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpResponseMessage gr =
            await appClient.GetAsync($"app/userdocuments/{documentId}"))
        {
            await gr.ValidateStatusCodeAsync(this.Logger);
            currentUserDocument = (await gr.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        var currentVersion = currentUserDocument[VersionKey];
        if (currentVersion?.ToString() != incomingVersion.ToString())
        {
            return this.BadRequest(new ODataError(
                code: "UserDocumentModified",
                message: "The version value specified in the input does not match the current" +
                    " version value."));
        }

        var proposalContent = new JsonObject
        {
            ["name"] = "set_user_document",
            ["args"] = new JsonObject
            {
                ["documentId"] = documentId,
                ["document"] = currentUserDocument
            }
        };

        var approvers = currentUserDocument[ApproversKey];
        if (approvers != null)
        {
            // Serialize and Deserialize to avoid "The node already has a parent." if AsObject() is
            // used directly.
            proposalContent["approvers"] = JsonSerializer.Deserialize<JsonArray>(
                JsonSerializer.Serialize(approvers.AsArray()))!;
        }

        return await this.SubmitUserProposal(proposalContent);
    }

    [HttpPost("/userdocuments/{documentId}/vote_accept")]
    public Task<IActionResult> VoteAccept(
        [FromRoute] string documentId,
        [FromBody] JsonObject content)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "accepted"
        };
        return this.Vote(documentId, content, ballot);
    }

    [HttpPost("/userdocuments/{documentId}/vote_reject")]
    public Task<IActionResult> VoteReject(
        [FromRoute] string documentId,
        [FromBody] JsonObject content)
    {
        var ballot = new JsonObject
        {
            ["ballot"] = "rejected"
        };
        return this.Vote(documentId, content, ballot);
    }

    [HttpPost("/userdocuments/{documentId}/vote")]
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
        var proposalId = content[ProposalIdKey]?.ToString();
        if (proposalId == null)
        {
            return this.BadRequest(new ODataError(
                code: "ProposalIdMissing",
                message: "Musty specify the proposalId to vote on."));
        }

        JsonObject currentUserDocument;
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpResponseMessage gr =
            await appClient.GetAsync($"app/userdocuments/{documentId}"))
        {
            await gr.ValidateStatusCodeAsync(this.Logger);
            currentUserDocument = (await gr.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        var currentProposalId = currentUserDocument[ProposalIdKey];
        if (currentProposalId == null || string.IsNullOrEmpty(currentProposalId.ToString()))
        {
            return this.BadRequest(new ODataError(
                code: "UserDocumentNotProposed",
                message: $"Cannot vote on the document as it is currently not proposed. " +
                    $"UserDocument state is: {currentUserDocument[StateKey]?.ToString()}"));
        }

        if (currentProposalId.ToString() != proposalId)
        {
            return this.BadRequest(new ODataError(
                code: "ProposalIdMismatch",
                message: $"The proposalId '{proposalId}' specified in the input " +
                    $"does not match the current proposalId '{currentProposalId}' value."));
        }

        return await this.SubmitVote(proposalId, ballot);
    }
}
