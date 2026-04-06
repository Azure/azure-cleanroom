// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AttestationClient;

public class AttestationReport
{
    [JsonPropertyName("snpCACI")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnpCACIAttestationReport? SnpCaci { get; set; }

    [JsonPropertyName("snpCvm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SnpCvmAttestationReport? SnpCvm { get; set; }
}