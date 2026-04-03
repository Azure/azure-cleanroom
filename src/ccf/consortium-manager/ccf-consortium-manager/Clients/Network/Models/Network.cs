// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Node.Models;

public class Network
{
    [JsonPropertyName("service_certificate")]
    public string ServiceCert { get; set; } = default!;
}
