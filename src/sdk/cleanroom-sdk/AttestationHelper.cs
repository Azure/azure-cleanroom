// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AttestationClient;
using Azure.Cleanroom.Governance.Client;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// Helper for constructing the generated HTTP client's typed attestation
/// models and decrypting attestation-encrypted responses.
/// </summary>
/// <remarks>
/// Initialized at client build time with credentials from the builder.
/// The <see cref="ICleanroomClient"/> implementation uses this to
/// auto-inject attestation into request models and auto-decrypt responses.
/// </remarks>
public class AttestationHelper
{
    private readonly AttestationCredentials credentials;

    public AttestationHelper(AttestationCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        this.credentials = credentials;
    }

    /// <summary>
    /// Gets the PEM-encoded public key for response encryption.
    /// </summary>
    public string PublicKeyPem => this.credentials.PublicKeyPem;

    public SnpEvidence BuildEvidence()
    {
        var report = this.credentials.Report;
        if (report == null)
        {
            // No report (JWT mode with ephemeral keypair).
            return new SnpEvidence(
                evidence: string.Empty,
                endorsements: string.Empty,
                uvmEndorsements: string.Empty);
        }

        if (report.SnpCaci != null)
        {
            return new SnpEvidence(
                evidence: report.SnpCaci.Attestation ?? string.Empty,
                endorsements: report.SnpCaci.PlatformCertificates
                    ?? string.Empty,
                uvmEndorsements: report.SnpCaci.UvmEndorsements
                    ?? string.Empty);
        }

        if (report.SnpCvm != null)
        {
            // CVM reports serialize the entire report as evidence.
            var cvmJson = JsonSerializer.Serialize(report.SnpCvm);
            return new SnpEvidence(
                evidence: Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(cvmJson)),
                endorsements: string.Empty,
                uvmEndorsements: string.Empty);
        }

        throw new InvalidOperationException(
            "Attestation report has no CACI or CVM data.");
    }

    public Encrypt BuildEncrypt()
    {
        return new Encrypt(
            publicKey: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(this.PublicKeyPem)));
    }

    public Sign BuildSign(byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(this.credentials.PrivateKeyPem);

        var signature = rsa.SignData(
            data,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss);

        return new Sign(
            signature: Convert.ToBase64String(signature),
            publicKey: Convert.ToBase64String(
                Encoding.UTF8.GetBytes(this.PublicKeyPem)));
    }

    public T Decrypt<T>(string base64WrappedValue)
    {
        byte[] wrappedValue = Convert.FromBase64String(base64WrappedValue);
        byte[] decrypted = Attestation.UnwrapRsaOaepAesKwpValue(
            wrappedValue,
            this.credentials.PrivateKeyPem);
        string json = Encoding.UTF8.GetString(decrypted);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    public string DecryptString(string base64WrappedValue)
    {
        byte[] wrappedValue = Convert.FromBase64String(base64WrappedValue);
        byte[] decrypted = Attestation.UnwrapRsaOaepAesKwpValue(
            wrappedValue,
            this.credentials.PrivateKeyPem);
        return Encoding.UTF8.GetString(decrypted);
    }
}