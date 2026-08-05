// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AttestationClient;

// This is the json schema in which data is sent to CCF app endpoints.
// Structured as { vtpm: { evidence, nonce }, gpu?: { evidences }, userDataDocument: "..." }.
// userDataDocument is the base64-encoded canonical JSON user data document
// whose SHA256 hash is bound to the runtime claims user-data[0:32].
public class CcfSnpCvmAttestationReport
{
    [JsonPropertyName("vtpm")]
    public CcfVTpmAttestationInput VTpm { get; set; } = default!;

    [JsonPropertyName("gpu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GpuAttestationReport? Gpu { get; set; }

    [JsonPropertyName("userDataDocument")]
    public string UserDataDocument { get; set; } = default!;

    public static CcfSnpCvmAttestationReport ConvertFrom(SnpCvmAttestationReport r)
    {
        return new CcfSnpCvmAttestationReport
        {
            VTpm = new CcfVTpmAttestationInput
            {
                Evidence = new CcfSnpCvmAttestationEvidence
                {
                    TpmQuote = r.VTpm.Evidence.TpmQuote,
                    HclReport = r.VTpm.Evidence.HclReport,
                    SnpReport = r.VTpm.Evidence.SnpReport,
                    AikCert = r.VTpm.Evidence.AikCert,
                    Pcrs = r.VTpm.Evidence.Pcrs
                },
                Nonce = r.VTpm.Nonce,
                PlatformCertificates = r.VTpm.PlatformCertificates
            },
            Gpu = r.Gpu,
            UserDataDocument = r.UserDataDocument
        };
    }

    public JsonObject AsObject()
    {
        return JsonSerializer.Deserialize<JsonObject>(JsonSerializer.Serialize(this))!;
    }
}

// vTPM attestation input sent to CCF.
public class CcfVTpmAttestationInput
{
    [JsonPropertyName("evidence")]
    public CcfSnpCvmAttestationEvidence Evidence { get; set; } = default!;

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = default!;

    [JsonPropertyName("platformCertificates")]
    public string PlatformCertificates { get; set; } = default!;
}

public class CcfSnpCvmAttestationEvidence
{
    [JsonPropertyName("tpmQuote")]
    public string TpmQuote { get; set; } = default!;

    [JsonPropertyName("hclReport")]
    public string HclReport { get; set; } = default!;

    [JsonPropertyName("snpReport")]
    public string SnpReport { get; set; } = default!;

    [JsonPropertyName("aikCert")]
    public string AikCert { get; set; } = default!;

    [JsonPropertyName("pcrs")]
    public Dictionary<string, string> Pcrs { get; set; } = default!;
}