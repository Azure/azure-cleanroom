// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;

namespace CleanRoomProvider;

public class AciConstants
{
    public const string AllowAllPolicyRegoBase64 =
        "cGFja2FnZSBwb2xpY3kKCmFwaV9zdm4gOj0gIjAuMTAuMCIKCm1vdW50X2RldmljZSA"
        + "6PSB7ImFsbG93ZWQiOiB0cnVlfQptb3VudF9vdmVybGF5IDo9IHsiYWxsb3dlZCI6I"
        + "HRydWV9CmNyZWF0ZV9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSwgImVudl9"
        + "saXN0IjogbnVsbCwgImFsbG93X3N0ZGlvX2FjY2VzcyI6IHRydWV9CnVubW91bnRfZGV"
        + "2aWNlIDo9IHsiYWxsb3dlZCI6IHRydWV9IAp1bm1vdW50X292ZXJsYXkgOj0geyJhbGx"
        + "vd2VkIjogdHJ1ZX0KZXhlY19pbl9jb250YWluZXIgOj0geyJhbGxvd2VkIjogdHJ1ZSw"
        + "gImVudl9saXN0IjogbnVsbH0KZXhlY19leHRlcm5hbCA6PSB7ImFsbG93ZWQiOiB0cnV"
        + "lLCAiZW52X2xpc3QiOiBudWxsLCAiYWxsb3dfc3RkaW9fYWNjZXNzIjogdHJ1ZX0Kc2h"
        + "1dGRvd25fY29udGFpbmVyIDo9IHsiYWxsb3dlZCI6IHRydWV9CnNpZ25hbF9jb250YWl"
        + "uZXJfcHJvY2VzcyA6PSB7ImFsbG93ZWQiOiB0cnVlfQpwbGFuOV9tb3VudCA6PSB7ImF"
        + "sbG93ZWQiOiB0cnVlfQpwbGFuOV91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cmd"
        + "ldF9wcm9wZXJ0aWVzIDo9IHsiYWxsb3dlZCI6IHRydWV9CmR1bXBfc3RhY2tzIDo9IHs"
        + "iYWxsb3dlZCI6IHRydWV9CnJ1bnRpbWVfbG9nZ2luZyA6PSB7ImFsbG93ZWQiOiB0cnV"
        + "lfQpsb2FkX2ZyYWdtZW50IDo9IHsiYWxsb3dlZCI6IHRydWV9CnNjcmF0Y2hfbW91bnQ"
        + "gOj0geyJhbGxvd2VkIjogdHJ1ZX0Kc2NyYXRjaF91bm1vdW50IDo9IHsiYWxsb3dlZCI6IHRydWV9Cg==";

    public static readonly string AllowAllPolicyRego =
        Encoding.UTF8.GetString(Convert.FromBase64String(AllowAllPolicyRegoBase64));

    public static readonly string AllowAllPolicyDigest =
        BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(AllowAllPolicyRego)))
        .Replace("-", string.Empty).ToLower();

    public class ContainerName
    {
        public const string CcrAttestation = "ccr-attestation";
        public const string CcrProxy = "ccr-proxy";
        public const string CcrGovernance = "ccr-governance";
        public const string AnalyticsAgent = "cleanroom-spark-analytics-agent";
        public const string SparkFrontend = "cleanroom-spark-frontend";
        public const string Skr = "skr";
        public const string OtelCollector = "otel-collector";
    }
}
