// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class CollaborationReportResponse
{
    [JsonPropertyName("cgs")]
    public required CgsReportInfo Cgs { get; set; }

    [JsonPropertyName("consortiumManager")]
    public required ConsortiumManagerReportInfo ConsortiumManager { get; set; }
}

public class CgsReportInfo
{
    [JsonPropertyName("cgsEndpoint")]
    public required string CgsEndpoint { get; set; }

    [JsonPropertyName("recoveryAgentEndpoint")]
    public required string RecoveryAgentEndpoint { get; set; }

    [JsonPropertyName("report")]
    public required CcfAttestationReportResponse Report { get; set; }
}

public class ConsortiumManagerReportInfo
{
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; set; }

    [JsonPropertyName("report")]
    public required ConsortiumManagerReport Report { get; set; }
}
