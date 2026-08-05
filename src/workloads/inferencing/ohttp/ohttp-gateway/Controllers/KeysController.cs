// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Mvc;
using OhttpCommon;
using OhttpGateway;

namespace Controllers;

/// <summary>
/// Serves the HPKE public key config and attestation report.
/// </summary>
[ApiController]
public class KeysController : ControllerBase
{
    private readonly ILogger logger;
    private readonly HpkeKeyManager keyManager;

    public KeysController(ILogger logger, HpkeKeyManager keyManager)
    {
        this.logger = logger;
        this.keyManager = keyManager;
    }

    /// <summary>
    /// Returns the binary KeyConfig (RFC 9458 §3).
    /// </summary>
    /// <returns>The serialized KeyConfig bytes.</returns>
    [HttpGet("/ohttp-gateway/.well-known/ohttp-keys")]
    public IActionResult GetKeys()
    {
        this.logger.LogInformation("Serving OHTTP KeyConfig.");
        return this.File(
            this.keyManager.SerializedKeyConfig,
            OhttpConstants.OhttpKeysMediaType);
    }

    /// <summary>
    /// Returns the SNP attestation report binding SHA256(KeyConfig) as report_data.
    /// </summary>
    /// <returns>The attestation report and key config hash.</returns>
    [HttpGet("/ohttp-gateway/ohttp-keys/attestation")]
    public async Task<IActionResult> GetAttestation()
    {
        this.logger.LogInformation("Serving attestation report for OHTTP key.");

        var report = await this.keyManager.GetAttestationReportAsync();
        string keyHash = Convert.ToHexString(this.keyManager.KeyConfigHash);

        return this.Ok(new
        {
            keyConfigHash = keyHash,
            attestation = report
        });
    }
}
