// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;
#pragma warning disable MEN002 // Line is too long

public record GovernanceConfig(
    [property: JsonPropertyName("ccrgovEndpoint")] string? CcrgovEndpoint,
    [property: JsonPropertyName("serviceCert")] string? ServiceCert,
    [property: JsonPropertyName("serviceCertDiscovery")] CcfServiceCertDiscoveryModel? ServiceCertDiscovery);

#pragma warning restore MEN002 // Line is too long
