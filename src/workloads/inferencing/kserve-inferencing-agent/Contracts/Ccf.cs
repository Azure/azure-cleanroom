// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Controllers;

public static class Ccf
{
    public static bool IsRecoveryMember(MemberInfo m)
    {
        return !string.IsNullOrEmpty(m.PublicEncryptionKey);
    }

    public record MemberInfoList(
        [property: JsonPropertyName("value")] List<MemberInfo> Value);

    public record MemberInfo(
        [property: JsonPropertyName("certificate")] string Certificate,
        [property: JsonPropertyName("memberData")] JsonObject MemberData,
        [property: JsonPropertyName("memberId")] string MemberId,
        [property: JsonPropertyName("recoveryRole")] string RecoveryRole,
        [property: JsonPropertyName("publicEncryptionKey")] string PublicEncryptionKey,
        [property: JsonPropertyName("status")] string Status);
}
