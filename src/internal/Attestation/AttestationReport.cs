﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AttestationClient;

// This is the json schema format of the response of the ccr-attestation sidecar's fetch
// attestation api.
public class AttestationReport
{
    [JsonPropertyName("attestation")]
    public string Attestation { get; set; } = default!;

    [JsonPropertyName("platformCertificates")]
    public string PlatformCertificates { get; set; } = default!;

    [JsonPropertyName("uvmEndorsements")]
    public string UvmEndorsements { get; set; } = default!;
}