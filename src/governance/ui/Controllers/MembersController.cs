// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CgsUI.Models;
using Microsoft.AspNetCore.Mvc;

namespace CgsUI.Controllers;

public class MembersController : Controller
{
    private readonly ILogger<MembersController> logger;
    private readonly IConfiguration configuration;

    public MembersController(
        ILogger<MembersController> logger,
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
            var item = await client.GetFromJsonAsync<JsonObject>(
                $"{this.configuration.GetEndpoint()}/members");
            return this.View(new MembersViewModel
            {
                Members = item!.ToJsonString(new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                })
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

    public record ListMembers(
        [property: JsonPropertyName("value")] List<Member> Value);

    public record Member(
        [property: JsonPropertyName("certificate")] string Certificate,
        [property: JsonPropertyName("memberData")] MemberData MemberData,
        [property: JsonPropertyName("memberId")] string MemberId,
        [property: JsonPropertyName("recoveryRole")] string RecoveryRole,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("publicEncryptionKey")] string? PublicEncryptionKey);

    public record MemberData(
        [property: JsonPropertyName("identifier")] string Identifier,
        [property: JsonPropertyName("tenantId")] string? TenantId);
}
