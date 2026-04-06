// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class ListInvitationsResponse
{
    public required List<GetInvitationResponse> Value { get; set; }
}