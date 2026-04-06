// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AttestationClient;

// This is the json schema in which data is sent to CCF app endpoints. Terminology used in CCF
// is different from the skr sidecar's AttestationReport contract.
public class CcfSnpCACIAttestationReport
{
    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = default!;

    [JsonPropertyName("endorsements")]
    public string Endorsements { get; set; } = default!;

    [JsonPropertyName("uvm_endorsements")]
    public string UvmEndorsements { get; set; } = default!;

    public static CcfSnpCACIAttestationReport ConvertFrom(SnpCACIAttestationReport r)
    {
        return new CcfSnpCACIAttestationReport
        {
            Evidence = r.Attestation,
            Endorsements = r.PlatformCertificates,
            UvmEndorsements = r.UvmEndorsements
        };
    }

    public JsonObject AsObject()
    {
        return JsonSerializer.Deserialize<JsonObject>(JsonSerializer.Serialize(this))!;
    }
}