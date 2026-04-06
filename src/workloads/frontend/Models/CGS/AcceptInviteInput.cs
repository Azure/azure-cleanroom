// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class AcceptInviteInput
{
    public required string InvitationId { get; set; }

    public required string CollaborationId { get; set; }
}