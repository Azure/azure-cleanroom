// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using AttestationClient;

namespace CcfConsortiumMgr.Clients.RecoveryService.Models;

public class RecoveryServiceReport
{
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = default!;

    [JsonPropertyName("report")]
    public SnpCACIAttestationReport? Report { get; set; } = default!;

    [JsonPropertyName("serviceCert")]
    public string ServiceCert { get; set; } = default!;

    [JsonPropertyName("hostData")]
    public string? HostData { get; set; }
}
