// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class ContractData
{
    [JsonPropertyName("ccrgovEndpoint")]
    public string CcrgovEndpoint { get; set; } = default!;

    [JsonPropertyName("ccrgovApiPathPrefix")]
    public string CcrgovApiPathPrefix { get; set; } = default!;

    [JsonPropertyName("ccrgovServiceCert")]
    public string CcrgovServiceCert { get; set; } = default!;

    [JsonPropertyName("ccrgovServiceCertDiscovery")]
    public ServiceCertDiscoveryInput? CcrgovServiceCertDiscovery { get; set; }

    [JsonPropertyName("ccfNetworkRecoveryMembers")]
    public JsonArray? CcfNetworkRecoveryMembers { get; set; }
}