// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AttestationClient;

public class SnpCACIAttestationReport
{
    [JsonPropertyName("attestation")]
    public string Attestation { get; set; } = default!;

    [JsonPropertyName("platformCertificates")]
    public string PlatformCertificates { get; set; } = default!;

    [JsonPropertyName("uvmEndorsements")]
    public string UvmEndorsements { get; set; } = default!;
}
