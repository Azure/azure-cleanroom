// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AttestationClient;

// This is the JSON schema returned by the cvm-attestation-agent's /snp/attest endpoint.
// The response is structured as { vtpm: {...}, gpu?: {...}, userDataDocument: "..." }.
public class SnpCvmAttestationReport
{
    [JsonPropertyName("vtpm")]
    public VTpmAttestationReport VTpm { get; set; } = default!;

    [JsonPropertyName("gpu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GpuAttestationReport? Gpu { get; set; }

    // Base64-encoded canonical JSON user data document whose SHA-256 hash
    // is bound to the runtime claims user-data[0:32]. Transmitted alongside
    // evidence so the verifier can decode, re-hash, and compare against the
    // hardware-signed value.
    [JsonPropertyName("userDataDocument")]
    public string UserDataDocument { get; set; } = default!;
}

// vTPM attestation evidence and metadata.
public class VTpmAttestationReport
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

// GPU attestation evidence container.
public class GpuAttestationReport
{
    [JsonPropertyName("evidences")]
    public List<GpuDeviceEvidence> Evidences { get; set; } = default!;
}

// NVIDIA GPU attestation evidence for a single GPU.
public class GpuDeviceEvidence
{
    [JsonPropertyName("evidence")]
    public string Evidence { get; set; } = default!;

    [JsonPropertyName("certificate")]
    public string Certificate { get; set; } = default!;

    // Raw NVAT file-evidence fields preserved for verifier-side RIM appraisal.
    [JsonPropertyName("arch")]
    public string Arch { get; set; } = default!;

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = default!;
}

public class CvmRuntimeClaims
{
    [JsonPropertyName("keys")]
    public List<object>? Keys { get; set; }

    [JsonPropertyName("vm-configuration")]
    public CvmVmConfiguration? VmConfiguration { get; set; }

    // 64-byte user-data as a hex string. Bytes 0-31 are SHA256(user data
    // document); bytes 32-63 are zeros.
    // Serialized as the "user-data" key in runtime claims JSON.
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
