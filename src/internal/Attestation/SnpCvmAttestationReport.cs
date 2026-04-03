// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AttestationClient;

// This is the JSON schema returned by the cvm-attestation-agent's /snp/attest endpoint.
public class SnpCvmAttestationReport
{
    [JsonPropertyName("evidence")]
    public SnpCvmEvidence Evidence { get; set; } = default!;

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = default!;

    [JsonPropertyName("platformCertificates")]
    public string PlatformCertificates { get; set; } = default!;

    [JsonPropertyName("imageReference")]
    public SnpCvmImageReference ImageReference { get; set; } = default!;
}

// The attestation evidence collected from the CVM platform.
public class SnpCvmEvidence
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

    [JsonPropertyName("runtimeClaims")]
    public CvmRuntimeClaims RuntimeClaims { get; set; } = default!;

    public JsonObject AsObject()
    {
        return JsonSerializer.Deserialize<JsonObject>(JsonSerializer.Serialize(this))!;
    }
}

public class CvmRuntimeClaims
{
    [JsonPropertyName("keys")]
    public List<object>? Keys { get; set; }

    [JsonPropertyName("vm-configuration")]
    public CvmVmConfiguration? VmConfiguration { get; set; }

    [JsonPropertyName("user-data")]
    public string? UserData { get; set; }
}

public class SnpCvmImageReference
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = default!;

    [JsonPropertyName("offer")]
    public string Offer { get; set; } = default!;

    [JsonPropertyName("publisher")]
    public string Publisher { get; set; } = default!;

    [JsonPropertyName("sku")]
    public string Sku { get; set; } = default!;

    [JsonPropertyName("version")]
    public string Version { get; set; } = default!;

    [JsonPropertyName("communityGalleryImageId")]
    public string CommunityGalleryImageId { get; set; } = default!;

    [JsonPropertyName("sharedGalleryImageId")]
    public string SharedGalleryImageId { get; set; } = default!;

    [JsonPropertyName("exactVersion")]
    public string ExactVersion { get; set; } = default!;
}

public class CvmVmConfiguration
{
    [JsonPropertyName("root-cert-thumbprint")]
    public string? RootCertThumbprint { get; set; }

    [JsonPropertyName("console-enabled")]
    public bool ConsoleEnabled { get; set; }

    [JsonPropertyName("secure-boot")]
    public bool SecureBoot { get; set; }

    [JsonPropertyName("tpm-enabled")]
    public bool TpmEnabled { get; set; }

    [JsonPropertyName("tpm-persisted")]
    public bool TpmPersisted { get; set; }

    [JsonPropertyName("vmUniqueId")]
    public string? VmUniqueId { get; set; }
}
