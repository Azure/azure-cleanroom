// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Governance.Models;

public class Members
{
    [JsonPropertyName("value")]
    public List<Member> Value { get; set; } = default!;
}

public class Member
{
    [JsonPropertyName("memberData")]
    public MemberData MemberData { get; set; } = default!;

    [JsonPropertyName("memberId")]
    public string MemberId { get; set; } = default!;

    [JsonPropertyName("recoveryRole")]
    public string RecoveryRole { get; set; } = default!;

    [JsonPropertyName("certificate")]
    public string Certificate { get; set; } = default!;

    [JsonPropertyName("publicEncryptionKey")]
    public string PublicEncryptionKey { get; set; } = default!;
}

public class MemberData
{
    [JsonPropertyName("identifier")]
    public string Identifier { get; set; } = default!;

    [JsonPropertyName("isOperator")]
    public bool IsOperator { get; set; }

    [JsonPropertyName("isRecoveryOperator")]
    public bool IsRecoveryOperator { get; set; }

    [JsonPropertyName("recoveryService")]
    public RecoveryServiceData? RecoveryServiceData { get; set; }
}

public class RecoveryServiceData
{
    [JsonPropertyName("hostData")]
    public string? HostData { get; set; }
}
