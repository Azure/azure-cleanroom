// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using CgsUI.Models;
using Microsoft.AspNetCore.Mvc;

namespace CgsUI.Controllers;

public class UsersController : Controller
{
    private readonly ILogger<UsersController> logger;
    private readonly IConfiguration configuration;

    public UsersController(
        ILogger<UsersController> logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        try
        {
            using var client = new HttpClient();
            var users = (await client.GetFromJsonAsync<ListUsers>(
                $"{this.configuration.GetEndpoint()}/users"))!;
            var userInvitations = (await client.GetFromJsonAsync<ListInvitations>(
                $"{this.configuration.GetEndpoint()}/users/invitations"))!;
            List<UserViewModel> usersViewModel = [];
            foreach (var user in users.Value)
            {
                var invitation = userInvitations.Value?.Find(
                    i => i.InvitationId == user.InvitationId);
                usersViewModel.Add(new UserViewModel
                {
                    UserName = user.Data.Identifier,
                    UserId = user.Id,
                    InvitationId = user.InvitationId,
                    InvitationStatus = invitation?.Status
                });
            }

            var openInvitations = userInvitations.Value!.Where(
                i => !users.Value.Any(u => u.InvitationId == i.InvitationId));
            List<InvitationViewModel> openInvitationsViewModel = [];
            foreach (var oi in openInvitations)
            {
                openInvitationsViewModel.Add(new InvitationViewModel
                {
                    UserName = oi.Claims.PreferredUsername.First(),
                    InvitationId = oi.InvitationId,
                    InvitationStatus = oi.Status
                });
            }

            return this.View(new UsersAndInvitationsViewModel
            {
                Users = usersViewModel,
                OpenInvitations = openInvitationsViewModel
            });
        }
        catch (HttpRequestException re)
        {
            return this.View("Error", new ErrorViewModel
            {
                Content = re.Message
            });
        }
    }

    public record ListUsers(
    [property: JsonPropertyName("value")] List<UserInfo> Value);

    public record UserInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("accountType")] string AccountType,
    [property: JsonPropertyName("invitationId")] string InvitationId,
    [property: JsonPropertyName("data")] UserData Data);

    public record UserData(
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("identifier")] string Identifier);

    public record ListInvitations(
        [property: JsonPropertyName("value")] List<Invitation> Value);

    public record Invitation(
        [property: JsonPropertyName("invitationId")] string InvitationId,
        [property: JsonPropertyName("accountType")] string AccountType,
        [property: JsonPropertyName("claims")] Claims Claims,
        [property: JsonPropertyName("userInfo")] InvitationUserInfo UserInfo,
        [property: JsonPropertyName("status")] string Status);

    public record Claims(
        [property: JsonPropertyName("preferred_username")] List<string> PreferredUsername);

    public record InvitationUserInfo(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("data")] UserData Data);
}
