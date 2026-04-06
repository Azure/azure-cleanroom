// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class BaseController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly IPolicyStore policyStore;

    public BaseController(
        ILogger logger,
        IConfiguration configuration,
        IPolicyStore policyStore)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.policyStore = policyStore;
    }

    protected async Task<AttesationReportInfo> VerifyAttestationReport(JsonObject content)
    {
        JsonObject? attestationJson = content["attestation"]?.AsObject();
        if (attestationJson == null)
        {
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "AttestationMissing",
                "attestation input must be supplied.");
        }

        var attestation = JsonSerializer.Deserialize<CcfSnpCACIAttestationReport>(attestationJson)!;
        SnpReport snpReport;
        try
        {
            snpReport = SnpReport.VerifySnpAttestation(
                attestation.Evidence,
                attestation.Endorsements,
                attestation.UvmEndorsements);
        }
        catch (Exception e)
        {
            this.logger.LogError(e, $"VerifySnpAttestation failed with {e.Message}.");
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "VerifySnpAttestationFailed",
                e.Message);
        }

        if (snpReport.GetIsDebuggable())
        {
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "TeeDebugModeEnabled",
                "TEE is in debug mode hence failing SNP attestation verification.");
        }

        var incomingHostData = snpReport.HostData;
        var networkJoinPolicy = await this.policyStore.GetNetworkJoinPolicy();
        if (networkJoinPolicy == null)
        {
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "SecurityPolicyNotSet",
                $"No security policy has been set to validate the claim 'hostData' Value " +
                $"'{incomingHostData}'");
        }

        if (!networkJoinPolicy.Snp.HostData.Any(v => v.ToUpper() == incomingHostData))
        {
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "HostDataMismatch",
                $"Attestation claim 'hostData' Value '{incomingHostData.ToLower()}' does not " +
                $"match any " +
                $"expected value(s): {JsonSerializer.Serialize(networkJoinPolicy)}");
        }

        var incomingReportData = snpReport.ReportData;

        string publicKey = this.GetPublicKey(content);
        var publicKeyReportData = Attestation.ToReportDataHashValue(
            Encoding.UTF8.GetBytes(publicKey));

        // A sha256 returns 32 bytes of data while attestation.report_data is 64 bytes
        // (128 chars in a hex string) in size, so need to pad 00s at the end to compare. That is:
        // attestation.report_data = sha256(data)) + 64x(0).
        var paddedPublicKeyReportData = publicKeyReportData + new string('0', 64);

        if (incomingReportData != paddedPublicKeyReportData)
        {
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "ReportDataMismatch",
                $"Attestation claim 'reportData' Value '{incomingReportData}' does not match " +
                $"expected value: {paddedPublicKeyReportData} (publicKey: {publicKey})");
        }

        return new AttesationReportInfo
        {
            PublicKey = publicKey,
            HostData = incomingHostData
        };
    }

    protected T GetSignedData<T>(string publicKey, JsonObject content)
    {
        var data = content["data"]?.ToString();
        if (string.IsNullOrEmpty(data))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "DataMissing",
                "data input must be supplied.");
        }

        var signature = content["sign"]?["signature"]?.ToString();
        if (string.IsNullOrEmpty(signature))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "SignatureMissing",
                "sign.signature input must be supplied.");
        }

        var dataBytes = Convert.FromBase64String(data);
        var signatureBytes = Convert.FromBase64String(signature);
        bool verified = Signing.VerifyDataUsingKey(
            dataBytes,
            signatureBytes,
            publicKey,
            RSASignaturePaddingMode.Pss);
        if (!verified)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "SignatureMismatch",
                "Signature verification failed.");
        }

        var dataJson = Encoding.UTF8.GetString(dataBytes);
        return JsonSerializer.Deserialize<T>(dataJson)!;
    }

    private string GetPublicKey(JsonObject content)
    {
        var base64Key = content["sign"]?["publicKey"]?.ToString();
        if (string.IsNullOrEmpty(base64Key))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                "PublicKeyMissing",
                "sign.publicKey input must be supplied.");
        }

        var keyBytes = Convert.FromBase64String(base64Key);
        var key = Encoding.UTF8.GetString(keyBytes);
        return key;
    }

    public class AttesationReportInfo
    {
        public string PublicKey { get; set; } = default!;

        public string HostData { get; set; } = default!;
    }
}