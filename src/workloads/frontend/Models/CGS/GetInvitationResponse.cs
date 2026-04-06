// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class GetInvitationResponse
{
    public required string InvitationId { get; set; }

    public required string AccountType { get; set; }

    public required string Status { get; set; }

    public UserInfo? UserInfo { get; set; }

    public Dictionary<string, List<string>>? Claims
    {
        get; set;
    }
}

public class UserInfo
{
    public required string UserId { get; set; }

    public required UserData Data { get; set; }
}

public class UserData
{
    public required string TenantId { get; set; }
}
