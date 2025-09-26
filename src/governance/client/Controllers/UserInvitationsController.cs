// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Mail;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

#pragma warning disable SA1300 // Element should begin with upper-case letter
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InvitationAccountType
{
    /// <summary>
    /// Microsoft accounts (work, school, personal) which may not be associated with any Azure
    /// acount.
    /// </summary>
    microsoft
}
#pragma warning restore SA1300 // Element should begin with upper-case letter

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdentityType
{
    /// <summary>
    /// Entra ID user accounts.
    /// </summary>
    User,

    /// <summary>
    /// Entra ID service principals.
    /// </summary>
    ServicePrincipal
}

[ApiController]
public class UserInvitationsController : ClientControllerBase
{
    public UserInvitationsController(
        ILogger<UserInvitationsController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
    }

    [HttpGet("/users/invitations")]
    public async Task<JsonObject> List()
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request = new(HttpMethod.Get, $"app/users/invitations"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }

    [HttpGet("/users/invitations/{invitationId}")]
    public async Task<JsonObject> Get([FromRoute] string invitationId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Get, $"app/users/invitations/{invitationId}"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            var jsonResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
            return jsonResponse!;
        }
    }

    [HttpPost("/users/invitations/{invitationId}/accept")]
    public async Task Accept([FromRoute] string invitationId)
    {
        var appClient = this.CcfClientManager.GetAppClient();
        using (HttpRequestMessage request =
            new(HttpMethod.Post, $"app/users/invitations/{invitationId}/accept"))
        {
            using HttpResponseMessage response = await appClient.SendAsync(request);
            this.Response.CopyHeaders(response.Headers);
            await response.ValidateStatusCodeAsync(this.Logger);
            await response.WaitAppTransactionCommittedAsync(this.Logger, this.CcfClientManager);
        }
    }

    [HttpPost("/users/invitations/propose")]
    public async Task<IActionResult> CreateProposal([FromBody] InvitationPropoalInput input)
    {
        if (string.IsNullOrEmpty(input.UserName))
        {
            return this.BadRequest(new ODataError(
                code: "UserNameMissing",
                message: $"User name must be specified."));
        }

        var invitationId = input.InvitationId ?? Guid.NewGuid().ToString("N");
        var claims = new JsonObject();
        if (input.IdentityType == IdentityType.User)
        {
            if (!IsValidEmail(input.UserName))
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidUserName",
                    message: $"User name must be an email address for user identity type."));
            }

            claims["preferred_username"] = input.UserName;
            if (!string.IsNullOrEmpty(input.TenantId))
            {
                claims["tid"] = input.TenantId;
            }

            static bool IsValidEmail(string email)
            {
                try
                {
                    var addr = new MailAddress(email);
                    return addr.Address == email;
                }
                catch
                {
                    return false;
                }
            }
        }
        else if (input.IdentityType == IdentityType.ServicePrincipal)
        {
            if (!Guid.TryParse(input.UserName, out var _))
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidGuidUserName",
                    message: $"User name must be a GUID value for service principal " +
                    $"identity type."));
            }

            if (string.IsNullOrEmpty(input.TenantId))
            {
                return this.BadRequest(new ODataError(
                    code: "TenantIdMissing",
                    message: $"Tenant Id must be specified."));
            }

            claims["appid"] = input.UserName;
            claims["tid"] = input.TenantId;
        }
        else
        {
            throw new NotSupportedException($"{input.IdentityType} not handled. Fix this.");
        }

        var proposalContent = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "set_user_invitation_by_jwt_claims",
                    ["args"] = new JsonObject
                    {
                        ["invitationId"] = invitationId,
                        ["type"] = "add",
                        ["accountType"] = input.AccountType.ToString(),
                        ["claims"] = claims
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
        var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

        jsonResponse["invitationId"] = invitationId;
        return this.Ok(jsonResponse);
    }

    public class InvitationPropoalInput
    {
        public string? InvitationId { get; set; }

        public string UserName { get; set; } = default!;

        public string? TenantId { get; set; }

        public IdentityType IdentityType { get; set; }

        public InvitationAccountType AccountType { get; set; } = default!;
    }
}
