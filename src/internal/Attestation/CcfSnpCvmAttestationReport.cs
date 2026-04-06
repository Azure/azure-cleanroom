// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AttestationClient;

// This is the json schema in which data is sent to CCF app endpoints.
public class CcfSnpCvmAttestationReport
{
    [JsonPropertyName("evidence")]
    public CcfSnpCvmAttestationEvidence Evidence { get; set; } = default!;

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = default!;

    [JsonPropertyName("platformCertificates")]
    public string PlatformCertificates { get; set; } = default!;

    public static CcfSnpCvmAttestationReport ConvertFrom(SnpCvmAttestationReport r)
    {
        return new CcfSnpCvmAttestationReport
        {
            Evidence = new CcfSnpCvmAttestationEvidence
            {
                TpmQuote = r.Evidence.TpmQuote,
                HclReport = r.Evidence.HclReport,
                SnpReport = r.Evidence.SnpReport,
                AikCert = r.Evidence.AikCert,
                Pcrs = r.Evidence.Pcrs
            },
            Nonce = r.Nonce,
            PlatformCertificates = r.PlatformCertificates
        };
    }

    public JsonObject AsObject()
    {
        return JsonSerializer.Deserialize<JsonObject>(JsonSerializer.Serialize(this))!;
    }
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