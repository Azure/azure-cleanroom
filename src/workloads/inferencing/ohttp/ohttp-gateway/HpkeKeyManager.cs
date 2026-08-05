// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using AttestationClient;
using OhttpCommon;

namespace OhttpGateway;

/// <summary>
/// Manages the HPKE key pair and associated attestation report for the OHTTP server.
/// Generates an ephemeral P-384 key pair on startup and binds it to the TEE via
/// SNP attestation (report_data = SHA256(KeyConfig)).
/// </summary>
public class HpkeKeyManager
{
    private readonly ILogger logger;
    private readonly HpkeKeyPair keyPair;
    private readonly KeyConfig keyConfig;
    private readonly byte[] serializedKeyConfig;
    private AttestationReport? attestationReport;

    public HpkeKeyManager(ILogger logger)
    {
        this.logger = logger;

        // Generate ephemeral X25519 key pair.
        this.keyPair = Hpke.GenerateKeyPair();
        this.keyConfig = KeyConfig.CreateDefault(
            OhttpConstants.DefaultKeyId,
            this.keyPair.PublicKey);
        this.serializedKeyConfig = this.keyConfig.Serialize();

        this.logger.LogInformation(
            "Generated HPKE key pair. KeyConfig size: {Size} bytes.",
            this.serializedKeyConfig.Length);
    }

    /// <summary>
    /// Gets the serialized binary KeyConfig (application/ohttp-keys).
    /// </summary>
    public byte[] SerializedKeyConfig => this.serializedKeyConfig;

    /// <summary>
    /// Gets the KeyConfig object.
    /// </summary>
    public KeyConfig KeyConfig => this.keyConfig;

    /// <summary>
    /// Gets the private key bytes (P-384).
    /// </summary>
    public byte[] PrivateKey => this.keyPair.PrivateKey;

    /// <summary>
    /// Gets the SHA-256 hash of the serialized KeyConfig (used as report_data).
    /// </summary>
    public byte[] KeyConfigHash => SHA256.HashData(this.serializedKeyConfig);

    /// <summary>
    /// Fetches an SNP attestation report with report_data = SHA256(KeyConfig) and caches it.
    /// </summary>
    /// <returns>The cached or newly fetched attestation report.</returns>
    public async Task<SnpCACIAttestationReport> GetAttestationReportAsync()
    {
        if (this.attestationReport != null)
        {
            return this.attestationReport.SnpCaci!;
        }

        this.logger.LogInformation("Requesting SNP attestation report from SKR...");
        this.attestationReport = await Attestation.GetCACIReportAsync(this.serializedKeyConfig);
        this.logger.LogInformation("SNP attestation report obtained and cached.");

        return this.attestationReport.SnpCaci!;
    }

    /// <summary>
    /// Creates a new OhttpDecapsulator for processing a request with this key pair.
    /// </summary>
    /// <returns>A new decapsulator initialized with the server's key pair.</returns>
    public OhttpDecapsulator CreateDecapsulator()
    {
        return new OhttpDecapsulator(this.keyConfig, this.keyPair.PrivateKey);
    }
}
