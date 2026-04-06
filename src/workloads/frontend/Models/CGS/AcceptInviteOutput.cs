// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class AcceptInviteOutput
{
    public required string InvitationId { get; set; }

    public required string Status { get; set; }

    public string? Message { get; set; }
}