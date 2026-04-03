// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

/// <summary>
/// SKR (Secure Key Release) policy structure for cleanroom attestation.
/// This represents the policy used for key release and attestation verification.
/// </summary>
public class SkrPolicyResponse
{
    [JsonPropertyName("anyOf")]
    public required List<SkrPolicyAnyOf> AnyOf { get; set; }

    [JsonPropertyName("version")]
    public required string Version { get; set; }
}

public class SkrPolicyAnyOf
{
    [JsonPropertyName("allOf")]
    public required List<SkrPolicyClaim> AllOf { get; set; }

    [JsonPropertyName("authority")]
    public required string Authority { get; set; }
}

public class SkrPolicyClaim
{
    [JsonPropertyName("claim")]
    public required string Claim { get; set; }

    [JsonPropertyName("equals")]
    public required string EqualsValue { get; set; }
}