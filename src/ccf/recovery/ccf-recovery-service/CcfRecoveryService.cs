// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AttestationClient;
using CcfCommon;
using CoseUtils;

namespace Controllers;

public class CcfRecoveryService
{
    private const string DefaultServiceCertLocation = "/app/service/service-cert.pem";

    private readonly ILogger logger;
    private readonly IMemberStore memberStore;
    private readonly string serviceCertLocation;

    public CcfRecoveryService(
        ILogger logger,
        string? serviceCertLocation,
        IMemberStore memberStore)
    {
        this.logger = logger;
        this.memberStore = memberStore;
        this.serviceCertLocation = serviceCertLocation ?? DefaultServiceCertLocation;
    }

    public async Task<RecoveryMember> GenerateMember(string memberName)
    {
        this.logger.LogInformation($"Generating member {memberName}.");
        var signingKeyInfo = await this.memberStore.GenerateSigningKey(memberName);
        var encKeyInfo = await this.memberStore.GenerateEncryptionKey(memberName);

        var member = new RecoveryMember
        {
            EncryptionPublicKey = encKeyInfo.EncryptionPublicKey,
            SigningCert = signingKeyInfo.SigningCert,
            RecoveryService = new()
            {
                HostData = await CcfUtils.GetHostData()
            }
        };

        return member;
    }

    public async Task<RecoveryMember?> GetMember(string memberName)
    {
        var signingKeyInfo = await this.memberStore.GetSigningKey(memberName);
        var encKeyInfo = await this.memberStore.GetEncryptionKey(memberName);

        if (signingKeyInfo != null && encKeyInfo != null)
        {
            return new RecoveryMember
            {
                EncryptionPublicKey = encKeyInfo.EncryptionPublicKey,
                SigningCert = signingKeyInfo.SigningCert,
                RecoveryService = new()
                {
                    HostData = await CcfUtils.GetHostData()
                }
            };
        }

        return null;
    }

    public async Task<List<string>> GetMembers()
    {
        var members = await this.memberStore.GetMembers();
        return members;
    }

    public async Task<byte[]> GenerateStateDigestMessage(string memberName)
    {
        this.logger.LogInformation($"Generating state digest message for member {memberName}.");
        var signingKeyInfo = await this.memberStore.ReleaseSigningKey(memberName);
        if (signingKeyInfo == null)
        {
            throw new ApiException(
                HttpStatusCode.NotFound,
                "SigningKeyNotFound",
                "Signing key and/or member does not exist.");
        }

        var message = await Cose.CreateGovCoseSign1Message(
            new CoseSignKey(signingKeyInfo.SigningCert, signingKeyInfo.SigningKey),
            GovMessageType.StateDigest,
            payload: null);
        return message;
    }

    public async Task<RecoveryMemberReport?> GetMemberReport(string memberName)
    {
        var signingKeyInfo = await this.memberStore.GetSigningKey(memberName);
        var encKeyInfo = await this.memberStore.GetEncryptionKey(memberName);

        if (signingKeyInfo != null && encKeyInfo != null)
        {
            return new RecoveryMemberReport
            {
                EncryptionKeyReport = new ReportAndEncKey
                {
                    EncryptionPublicKey = encKeyInfo.EncryptionPublicKey,
                    Report = encKeyInfo.AttestationReport
                },
                SigningKeyReport = new ReportAndSigningCert
                {
                    SigningCert = signingKeyInfo.SigningCert,
                    Report = signingKeyInfo.AttestationReport
                }
            };
        }

        return null;
    }

    public async Task<byte[]> GenerateStateDigestAckMessage(
        string memberName,
        JsonObject stateDigest)
    {
        this.logger.LogInformation($"Generating state digest ack message for member {memberName}.");
        var signingKeyInfo = await this.memberStore.ReleaseSigningKey(memberName);
        if (signingKeyInfo == null)
        {
            throw new ApiException(
                HttpStatusCode.NotFound,
                "SigningKeyNotFound",
                "Signing key and/or member does not exist.");
        }

        var message = await Cose.CreateGovCoseSign1Message(
            new CoseSignKey(signingKeyInfo.SigningCert, signingKeyInfo.SigningKey),
            GovMessageType.Ack,
            payload: stateDigest.ToJsonString());
        return message;
    }

    public async Task<byte[]> GenerateRecoveryShareMessage(
        string memberName,
        JsonObject encryptedShareJson)
    {
        this.logger.LogInformation($"Generating recovery share message for member {memberName}.");
        var encryptionKeyInfo = await this.memberStore.ReleaseEncryptionKey(memberName);
        if (encryptionKeyInfo == null)
        {
            throw new ApiException(
                HttpStatusCode.NotFound,
                "EncryptionKeyNotFound",
                "Encryption key and/or member does not exist.");
        }

        var signingKeyInfo = await this.memberStore.ReleaseSigningKey(memberName);
        if (signingKeyInfo == null)
        {
            throw new ApiException(
                HttpStatusCode.NotFound,
                "SigningKeyNotFound",
                "Signing key and/or member does not exist.");
        }

        var encryptedShare = encryptedShareJson["encryptedShare"]!.ToString();
        byte[] wrappedValue = Convert.FromBase64String(encryptedShare);

        using var privateKey = RSA.Create();
        privateKey.ImportFromPem(encryptionKeyInfo.EncryptionPrivateKey);
        var decryptedShare = privateKey.Decrypt(wrappedValue, RSAEncryptionPadding.OaepSHA256);

        var wrappedDecryptedShare = Convert.ToBase64String(decryptedShare);
        JsonObject content = new()
        {
            ["share"] = wrappedDecryptedShare
        };

        var message = await Cose.CreateGovCoseSign1Message(
            new CoseSignKey(signingKeyInfo.SigningCert, signingKeyInfo.SigningKey),
            GovMessageType.RecoveryShare,
            payload: content.ToJsonString());
        return message;
    }

    public async Task<RecoveryServiceReport> GetServiceReport()
    {
        if (!Path.Exists(this.serviceCertLocation))
        {
            throw new ApiException(
                HttpStatusCode.ServiceUnavailable,
                "ServiceCertNotFound",
                "Could not locate the service certificate for this service.");
        }

        var serviceCert = await File.ReadAllTextAsync(this.serviceCertLocation);

        string platform;
        AttestationReport? report = null;
        if (CcfUtils.IsSevSnp())
        {
            platform = "snp";
            var bytes = Encoding.UTF8.GetBytes(serviceCert);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(bytes);
            report = await Attestation.GetReportAsync(hash);
        }
        else
        {
            platform = "virtual";
        }

        string hostData = await CcfUtils.GetHostData();
        return new RecoveryServiceReport
        {
            Platform = platform,
            Report = report,
            ServiceCert = serviceCert,
            HostData = hostData
        };
    }
}