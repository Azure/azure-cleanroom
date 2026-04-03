// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Governance.Models;

public class Invitation
{
    [JsonPropertyName("invitationId")]
    public string InvitationId { get; set; } = default!;

    [JsonPropertyName("status")]
    public string Status { get; set; } = default!;

    [JsonPropertyName("accountType")]
    public AccountType AccountType { get; set; } = default!;

    [JsonPropertyName("claims")]
    public UserClaims Claims { get; set; } = default!;

    [JsonPropertyName("userInfo")]
    public UserInfo UserInfo { get; set; } = default!;
}

public class UserClaims
{
    [JsonPropertyName("preferred_username")]
    public List<string> PreferredUsername { get; set; } = default!;

    [JsonPropertyName("appid")]
    public List<string> AppId { get; set; } = default!;
}

public class UserInfo
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = default!;

    [JsonPropertyName("data")]
    public UserData UserData { get; set; } = default!;
}

public class UserData
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = default!;
}
