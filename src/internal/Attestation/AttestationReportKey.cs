// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AttestationClient;

public class AttestationReportKey : KeyPair
{
    public AttestationReportKey(string publicKey, string privateKey, AttestationReport report)
        : base(publicKey, privateKey)
    {
        this.Report = report;
    }

    [JsonPropertyName("report")]
    public AttestationReport Report { get; } = default!;
}