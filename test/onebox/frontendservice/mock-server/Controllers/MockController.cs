// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FrontendSvc.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;

namespace MockSvc;

[ApiController]
public class MockController : ControllerBase
{
    private const string ExpectedAppId = "0ee284cd-4715-4fd8-8241-669d003cd5fa";
    private const string ExpectedScope = "0ee284cd-4715-4fd8-8241-669d003cd5fa/.default";
    private static Dictionary<string, string> userStatusByCollaboration = new();
    private static Dictionary<string, ConsortiumDetails> collaborationsList = new();
    private readonly ILogger<MockController> logger;

    public MockController(
        ILogger<MockController> logger)
    {
        this.logger = logger;
    }

    internal WebContext WebContext =>
        (WebContext)this.ControllerContext.HttpContext.Items[WebContext.WebContextIdentifer]!;

    [HttpGet("/ready")]
    public IActionResult Ready()
    {
        return this.Ok();
    }

    [HttpPost("/collaborations/{collaborationId}")]
    public IActionResult AddCollaborationDetails(
        [FromRoute] string collaborationId,
        [FromBody] ConsortiumDetails consortiumDetails)
    {
        collaborationsList[collaborationId] = consortiumDetails;
        this.logger.LogInformation($"Added consortium details for id: {collaborationId}");

        return this.Ok();
    }

    [HttpGet("/collaborations/{collaborationId}")]
    public IActionResult GetCollaboration(
        [FromRoute] string collaborationId,
        [FromHeader(Name = "x-ms-tenant-id")] string? tenantId = null,
        [FromHeader(Name = "x-ms-object-id")] string? objectId = null,
        [FromHeader(Name = "x-ms-user-email")] string? userEmail = null)
    {
        GetCollaborationOutput result;

        if (collaborationsList.TryGetValue(collaborationId, out var consortiumDetails))
        {
            this.logger.LogInformation($"Found consortium details for id: {collaborationId}");
            var userStatus = userStatusByCollaboration.TryGetValue(collaborationId, out var status)
            ? status
            : string.Empty;
            result = new GetCollaborationOutput
            {
                CollaborationId = collaborationId,
                CollaborationName = collaborationId,
                UserStatus = userStatus,
                ConsortiumEndpoint = consortiumDetails.CCFEndpoint,
                ConsortiumServiceCertificatePem = consortiumDetails.CCFServiceCertPem,
            };
        }
        else
        {
            this.logger.LogWarning($"No consortium details found for id: {collaborationId}");
            return this.NotFound();
        }

        return this.Ok(result);
    }

    [HttpGet("/collaborations")]
    public IActionResult GetAllCollaborations(
        [FromHeader(Name = "x-ms-tenant-id")] string? tenantId = null,
        [FromHeader(Name = "x-ms-object-id")] string? objectId = null,
        [FromHeader(Name = "x-ms-user-email")] string? userEmail = null)
    {
        this.logger.LogInformation("Retrieving all collaborations");

        var collaborations = collaborationsList.Select(kvp => new CollaborationOutput
        {
            CollaborationId = kvp.Key,
            CollaborationName = kvp.Key,
            UserStatus = userStatusByCollaboration.GetValueOrDefault(kvp.Key, string.Empty)
        }).ToList();

        var result = new ListCollaborationsOutput
        {
            Collaborations = collaborations
        };

        return this.Ok(result);
    }

    public class ConsortiumDetails
    {
        [Required]
        [JsonPropertyName("ccfEndpoint")]
        public string CCFEndpoint { get; set; } = default!;

        [Required]
        [JsonPropertyName("ccfServiceCertPem")]
        public string CCFServiceCertPem { get; set; } = default!;
    }

    public class ActivateUserInput
    {
        [JsonPropertyName("ccfEndpoint")]
        public string CcfEndpoint { get; set; } = default!;

        [JsonPropertyName("ccfServiceCertPem")]
        public string CcfServiceCertPem { get; set; } = default!;

        [JsonPropertyName("invitationId")]
        public string InvitationId { get; set; } = default!;
    }

    public class ActivateUserOutput
    {
        [JsonPropertyName("invitationId")]
        public string InvitationId { get; set; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; set; } = default!;

        [JsonPropertyName("message")]
        public string Message { get; set; } = default!;
    }

    public class UpdateCollaborationRequest
    {
        [JsonPropertyName("userEmail")]
        public string? UserEmail { get; set; }

        [JsonPropertyName("userIdentity")]
        public object? UserIdentity { get; set; }

        [JsonPropertyName("userStatus")]
        public string UserStatus { get; set; } = default!;
    }
}