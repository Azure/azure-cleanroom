// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// Member credentials for COSE-signed governance operations.
/// </summary>
public class MemberCredentials
{
    /// <summary>
    /// Gets or sets the signing certificate in PEM format.
    /// </summary>
    public string SigningCertificatePem { get; set; } = default!;

    /// <summary>
    /// Gets or sets the private key in PEM format for COSE signing.
    /// </summary>
    public string SigningKeyPem { get; set; } = default!;

    /// <summary>
    /// Gets or sets the member ID. Will be computed from certificate if not provided.
    /// </summary>
    public string? MemberId { get; set; }
}

/// <summary>
/// Attestation credentials for confidential computing scenarios.
/// Built by <see cref="CleanroomClientBuilder.WithSnpAttestation"/>
/// or <see cref="CleanroomClientBuilder.WithSnpAttestationFromFile"/>.
/// </summary>
public class AttestationCredentials
{
    /// <summary>
    /// Gets or sets the RSA private key PEM for signing and decryption.
    /// </summary>
    public string PrivateKeyPem { get; set; } = default!;

    /// <summary>
    /// Gets or sets the public key in PEM format.
    /// </summary>
    public string PublicKeyPem { get; set; } = default!;

    /// <summary>
    /// Gets or sets the structured attestation report from the TEE.
    /// </summary>
    public AttestationClient.AttestationReport? Report { get; set; }
}
