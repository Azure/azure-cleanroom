// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AttestationClient;

namespace CcfConsortiumMgr.Clients.RecoveryAgent.Models;

public class NetworkReport : IJsonOnDeserialized
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = default!;

    [JsonPropertyName("report")]
    public SnpCACIAttestationReport? Report { get; set; } = default!;

    [JsonPropertyName("reportDataPayload")]
    public string ReportDataPayload { get; set; } = default!;

    [JsonIgnore]
    public string ConstitutionDigest { get; set; } = default!;

    [JsonIgnore]
    public string JsAppBundleDigest { get; set; } = default!;

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!string.IsNullOrEmpty(this.ReportDataPayload))
        {
            var reportData = JsonSerializer.Deserialize<JsonObject>(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(this.ReportDataPayload)))!;

            this.ConstitutionDigest = reportData["constitutionDigest"]!.GetValue<string>();
            this.JsAppBundleDigest = reportData["jsappBundleDigest"]!.GetValue<string>();
        }
    }
}
