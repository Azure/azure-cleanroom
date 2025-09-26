// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Controllers;

public static class Ccf
{
    public class MemberInfoList
    {
        [JsonPropertyName("value")]
        public List<MemberInfo> Value { get; set; } = default!;
    }

    public class MemberInfo
    {
        [JsonPropertyName("certificate")]
        public string Certificate { get; set; } = default!;

        [JsonPropertyName("memberData")]
        public JsonObject MemberData { get; set; } = default!;

        [JsonPropertyName("memberId")]
        public string MemberId { get; set; } = default!;

        [JsonPropertyName("recoveryRole")]
        public string RecoveryRole { get; set; } = default!;

        [JsonPropertyName("publicEncryptionKey")]
        public string PublicEncryptionKey { get; set; } = default!;

        [JsonPropertyName("status")]
        public string Status { get; set; } = default!;
    }

    public class JoinPolicyInfo
    {
        [JsonPropertyName("snp")]
        public SnpSection Snp { get; set; } = default!;

        [JsonPropertyName("measurements")]
        public JsonObject Measurements { get; set; } = default!;

        [JsonPropertyName("uvmEndorsements")]
        public JsonObject UvmEndorsements { get; set; } = default!;

        public class SnpSection
        {
            [JsonPropertyName("hostData")]
            public Dictionary<string, string> HostData { get; set; } = default!;
        }
    }
}
