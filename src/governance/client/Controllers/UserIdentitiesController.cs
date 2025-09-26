// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class UserIdentitiesController : ClientControllerBase
{
    public UserIdentitiesController(
        ILogger<UserIdentitiesController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/users/identities")]
    public async Task<JsonObject> Get()
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request = new(HttpMethod.Get, $"app/users/identities"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }

    [HttpGet("/users/identities/{identityId}")]
    public async Task<JsonObject> Get([FromRoute] string identityId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Get, $"app/users/identities/{identityId}"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }

    [HttpPost("/users/identities/add")]
    public async Task<IActionResult> AddUserIdentity([FromBody] JsonObject content)
    {
        string? objectId = content["objectId"]?.ToString();
        string? accountType = content["accountType"]?.ToString();
        string? tenantId = content["tenantId"]?.ToString();
        string? identifier = content["identifier"]?.ToString();
        string? invitationId = content["acceptedInvitationId"]?.ToString();
        if (string.IsNullOrEmpty(objectId) && string.IsNullOrEmpty(invitationId))
        {
            return this.BadRequest(new ODataError(
                code: "ObjectIdMissing",
                message: "Musty specify either the objectId or the accepted invitation Id."));
        }

        if (!string.IsNullOrEmpty(objectId) && !string.IsNullOrEmpty(invitationId))
        {
            return this.BadRequest(new ODataError(
                code: "TooManyInputs",
                message: "Only one of objectId or the accepted invitation Id must be specified."));
        }

        if (!string.IsNullOrEmpty(objectId) && string.IsNullOrEmpty(accountType))
        {
            return this.BadRequest(new ODataError(
                code: "AccountTypeNotSpecified",
                message: "Account type must be specified for the object Id."));
        }

        if (!string.IsNullOrEmpty(objectId) && string.IsNullOrEmpty(tenantId))
        {
            return this.BadRequest(new ODataError(
                code: "TenantIdNotSpecified",
                message: "Tenant Id must be specified for the object Id."));
        }

        if (!string.IsNullOrEmpty(invitationId))
        {
            if (!string.IsNullOrEmpty(accountType))
            {
                return this.BadRequest(new ODataError(
                    code: "CannotSpecifyAccountType",
                    message: "An account type cannot be specified along with accepted " +
                    "invitation Id."));
            }

            if (!string.IsNullOrEmpty(tenantId))
            {
                return this.BadRequest(new ODataError(
                    code: "CannotSpecifyTenantId",
                    message: "A tenant Id cannot be specified along with accepted " +
                    "invitation Id."));
            }

            if (!string.IsNullOrEmpty(identifier))
            {
                return this.BadRequest(new ODataError(
                    code: "CannotSpecifyIdentifier",
                    message: "An identifier cannot be specified along with accepted " +
                    "invitation Id."));
            }

            var appClient = this.CcfClientManager.GetAppClient();
            using (HttpRequestMessage invRequest =
                new(HttpMethod.Get, $"app/users/invitations/{invitationId}"))
            {
                using HttpResponseMessage invResponse = await appClient.SendAsync(invRequest);
                await invResponse.ValidateStatusCodeAsync(this.Logger);
                var invitation = (await invResponse.Content.ReadFromJsonAsync<Invitation>())!;
                if (invitation.Status != "Accepted")
                {
                    return this.BadRequest(new ODataError(
                        code: "InvitationNotAccepted",
                        message: $"Specified invitation is not in an accepted state." +
                        $" Invitation status: '{invitation.Status}'."));
                }

                accountType = invitation.AccountType;
                if (string.IsNullOrEmpty(accountType))
                {
                    return this.BadRequest(new ODataError(
                        code: "AccountTypeNotSetInAcceptedInvitation",
                        message: $"Specified invitation does not have .accountType set: " +
                        $"{JsonSerializer.Serialize(invitation)}"));
                }

                objectId = invitation.UserInfo?.UserId;
                if (string.IsNullOrEmpty(objectId))
                {
                    return this.BadRequest(new ODataError(
                        code: "UserIdNotSetInAcceptedInvitation",
                        message: $"Specified invitation does not have .userInfo.userId set: " +
                        $"{JsonSerializer.Serialize(invitation)}"));
                }

                tenantId = invitation.UserInfo?.Data?.TenantId;
                if (string.IsNullOrEmpty(tenantId))
                {
                    return this.BadRequest(new ODataError(
                        code: "TenantIdNotSetInAcceptedInvitation",
                        message: $"Specified invitation does not have .userInfo.data.tenantId " +
                        $"set: {JsonSerializer.Serialize(invitation)}"));
                }

                identifier = invitation.Claims?.PreferredUsername?.First() ??
                    invitation.Claims?.AppId.First();
                if (string.IsNullOrEmpty(identifier))
                {
                    return this.BadRequest(new ODataError(
                        code: "UsernameClaimNotSetInAcceptedInvitation",
                        message: $"Specified invitation does not have " +
                        $".claims.[preferred_username|appid] " +
                        $"set: {JsonSerializer.Serialize(invitation)}"));
                }
            }
        }

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "set_user_identity",
                    ["args"] = new JsonObject
                    {
                        ["id"] = objectId,
                        ["accountType"] = accountType,
                        ["invitationId"] = invitationId,
                        ["data"] = new JsonObject
                        {
                            ["tenantId"] = tenantId,
                            ["identifier"] = identifier
                        }
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

    public record Invitation(
        [property: JsonPropertyName("invitationId")] string InvitationId,
        [property: JsonPropertyName("accountType")] string AccountType,
        [property: JsonPropertyName("claims")] Claims Claims,
        [property: JsonPropertyName("userInfo")] UserInvitationInfo UserInfo,
        [property: JsonPropertyName("status")] string Status);

    public record Claims(
        [property: JsonPropertyName("preferred_username")] List<string> PreferredUsername,
        [property: JsonPropertyName("appid")] List<string> AppId);

    public record UserInvitationInfo(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("data")] UserData Data);

    public record UserData(
        [property: JsonPropertyName("tenantId")] string TenantId);
}
