// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class ServiceCertDiscoveryInput
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; set; } = default!;

    [JsonPropertyName("snpHostData")]
    public string SnpHostData { get; set; } = default!;

    [JsonPropertyName("skipDigestCheck")]
    public bool SkipDigestCheck { get; set; } = default!;

    [JsonPropertyName("constitutionDigest")]
    public string ConstitutionDigest { get; set; } = default!;

    [JsonPropertyName("jsappBundleDigest")]
    public string JsappBundleDigest { get; set; } = default!;
}
