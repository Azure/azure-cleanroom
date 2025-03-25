// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Secrets;
using CcfCommon;
using Controllers;

namespace CcfProviderClient;

public class AkvKeyStore : IKeyStore
{
    private const string KeyIdTag = "ccf-key-id";
    private const string KeyTypeTag = "ccf-key-type";

    private readonly ILogger logger;
    private readonly string skrEndpoint;
    private readonly string akvEndpoint;
    private readonly string maaEndpoint;

    public AkvKeyStore(
        ILogger logger,
        string skrEndpoint,
        string akvEndpoint,
        string maaEndpoint)
    {
        if (string.IsNullOrEmpty(skrEndpoint))
        {
            throw new ArgumentNullException(nameof(skrEndpoint));
        }

        if (string.IsNullOrEmpty(akvEndpoint))
        {
            throw new ArgumentNullException(nameof(akvEndpoint));
        }

        if (string.IsNullOrEmpty(maaEndpoint))
        {
            throw new ArgumentNullException(nameof(maaEndpoint));
        }

        this.logger = logger;
        if (!skrEndpoint.StartsWith("http"))
        {
            skrEndpoint = "http://" + skrEndpoint;
        }

        this.skrEndpoint = skrEndpoint;

        if (!akvEndpoint.StartsWith("http"))
        {
            akvEndpoint = "https://" + akvEndpoint;
        }

        this.akvEndpoint = akvEndpoint;

        if (!maaEndpoint.StartsWith("http"))
        {
            maaEndpoint = "https://" + maaEndpoint;
        }

        this.maaEndpoint = maaEndpoint;
    }

    public async Task<EncryptionKeyInfo?> GetEncryptionKey(string kid)
    {
        var creds = new DefaultAzureCredential();
        string keyName = ToEncKeyName(kid);
        var jsonWebKey = await this.GetKey(creds, keyName);
        if (jsonWebKey == null)
        {
            return null;
        }

        using var rsaKey = jsonWebKey.ToRSA();
        var publicKey = rsaKey.ExportSubjectPublicKeyInfoPem();
        var report = await this.GetAttestationReportSecret(creds, publicKey);
        return new EncryptionKeyInfo
        {
            AttestationReport = report,
            EncryptionPublicKey = publicKey
        };
    }

    public async Task<EncryptionKeyInfo> GenerateEncryptionKey(
        string kid,
        string kty,
        Dictionary<string, string> tags)
    {
        string keyName = ToEncKeyName(kid);
        AttestationReportKey encKeyAndReport;
        string publicKey;

        var existingKeyInfo = await this.GetEncryptionKey(keyName);
        if (existingKeyInfo != null)
        {
            return existingKeyInfo;
        }

        var creds = new DefaultAzureCredential();
        if (CcfUtils.IsSevSnp())
        {
            encKeyAndReport = await Attestation.GenerateRsaKeyPairAndReportAsync();
        }
        else
        {
            var attestationJson = await File.ReadAllTextAsync(
                "/app/insecure-virtual/encryption_key_attestation.json");
            encKeyAndReport = JsonSerializer.Deserialize<AttestationReportKey>(attestationJson)!;
        }

        // To handle idempotency we use the public key fingerprint to upload the attestation report
        // first. If import fails as the key with the same name already existed then worse we
        // have uploaded a report for a key pair which was never imported into KV.
        await this.SetAttestationReportSecret(
            creds,
            encKeyAndReport.PublicKey,
            encKeyAndReport.Report);

        // Even though we generated a key above its import might fail if a key was previous
        // imported with the same name. Import fails as keys are created with an immutable release
        // policy so attempting to re-import does not create a new version of the key but
        // fails as overwriting a release policy is not permitted.
        try
        {
            var keyTags = new Dictionary<string, string>
            {
                { KeyIdTag, kid },
                { KeyTypeTag, kty }
            };

            foreach (var t in tags)
            {
                keyTags.Add(t.Key, t.Value);
            }

            var snpReport = SnpReport.VerifySnpAttestation(
                encKeyAndReport.Report.Attestation,
                encKeyAndReport.Report.PlatformCertificates,
                encKeyAndReport.Report.UvmEndorsements);
            await this.ImportKey(
                creds,
                keyName,
                encKeyAndReport.PrivateKey,
                snpReport.HostData,
                KeyType.Rsa,
                keyTags);
            publicKey = encKeyAndReport.PublicKey;
        }
        catch (RequestFailedException rfe)
        when (rfe.Message.Contains("AKV.SKR.1020: Immutable Key Release Policy cannot be modified."))
        {
            var jsonWebKey = (await this.GetKey(creds, keyName))!;
            using var rsaKey = jsonWebKey.ToRSA();
            publicKey = rsaKey.ExportSubjectPublicKeyInfoPem();
        }

        var attestationReport = await this.GetAttestationReportSecret(creds, publicKey);

        return new EncryptionKeyInfo
        {
            AttestationReport = attestationReport,
            EncryptionPublicKey = publicKey
        };
    }

    public async Task<EncryptionPrivateKeyInfo> ReleaseEncryptionKey(string kid)
    {
        var creds = new DefaultAzureCredential();
        string keyName = ToEncKeyName(kid);

        var jsonWebKey = await this.ReleaseKey(keyName, creds);
        using var rsaKey = jsonWebKey.ToRSA(includePrivateParameters: true);

        var publicKey = rsaKey.ExportSubjectPublicKeyInfoPem();
        var attestationReport = await this.GetAttestationReportSecret(creds, publicKey);
        return new EncryptionPrivateKeyInfo
        {
            AttestationReport = attestationReport,
            EncryptionPrivateKey = rsaKey.ExportPkcs8PrivateKeyPem(),
            EncryptionPublicKey = publicKey
        };
    }

    public async Task<SigningKeyInfo?> GetSigningKey(string kid)
    {
        var creds = new DefaultAzureCredential();
        string keyName = ToSigningKeyName(kid);

        var jsonWebKey = await this.GetKey(creds, keyName);
        if (jsonWebKey == null)
        {
            return null;
        }

        using var ecdsaKey = jsonWebKey.ToECDsa();
        var publicKey = ecdsaKey.ExportSubjectPublicKeyInfoPem();

        var report = await this.GetAttestationReportSecret(creds, publicKey);
        var signingCert = await this.GetSigningCertSecret(creds, publicKey);
        return new SigningKeyInfo
        {
            AttestationReport = report,
            SigningCert = signingCert
        };
    }

    public async Task<SigningKeyInfo> GenerateSigningKey(
        string kid,
        string kty,
        Dictionary<string, string> tags)
    {
        var creds = new DefaultAzureCredential();
        string keyName = ToSigningKeyName(kid);

        AttestationReportKeyCert signingKeyAndReport;
        string publicKey;

        var existingKeyInfo = await this.GetSigningKey(keyName);
        if (existingKeyInfo != null)
        {
            return existingKeyInfo;
        }

        if (CcfUtils.IsSevSnp())
        {
            signingKeyAndReport = await Attestation.GenerateEcdsaKeyPairAndReportAsync();
        }
        else
        {
            var attestationJson = await File.ReadAllTextAsync(
                "/app/insecure-virtual/signing_key_attestation.json");
            signingKeyAndReport =
                JsonSerializer.Deserialize<AttestationReportKeyCert>(attestationJson)!;
        }

        // To handle idempotency we use the public key fingerprint to upload the attestation report
        // first. If import fails as the key with the same name already existed then worse we
        // have uploaded a report for a key pair which was never imported into KV.
        await this.SetAttestationReportSecret(
            creds,
            signingKeyAndReport.PublicKey,
            signingKeyAndReport.Report);
        await this.SetSigningCertSecret(
            creds,
            signingKeyAndReport.PublicKey,
            signingKeyAndReport.Certificate);

        // Even though we generated a key above its import might fail if a key was previous
        // imported with the same name. Import fails as keys are created with an immutable release
        // policy so attempting to re-import does not create a new version of the key but
        // fails as overwriting a release policy is not permitted.
        try
        {
            var keyTags = new Dictionary<string, string>
            {
                { KeyIdTag, kid },
                { KeyTypeTag, kty }
            };

            foreach (var t in tags)
            {
                keyTags.Add(t.Key, t.Value);
            }

            var snpReport = SnpReport.VerifySnpAttestation(
                signingKeyAndReport.Report.Attestation,
                signingKeyAndReport.Report.PlatformCertificates,
                signingKeyAndReport.Report.UvmEndorsements);
            await this.ImportKey(
                creds,
                keyName,
                signingKeyAndReport.PrivateKey,
                snpReport.HostData,
                KeyType.Ec,
                keyTags);
            publicKey = signingKeyAndReport.PublicKey;
        }
        catch (RequestFailedException rfe)
        when (rfe.Message.Contains("AKV.SKR.1020: Immutable Key Release Policy cannot be modified."))
        {
            var jsonWebKey = (await this.GetKey(creds, keyName))!;
            using var ecdsaKey = jsonWebKey.ToECDsa();
            publicKey = ecdsaKey.ExportSubjectPublicKeyInfoPem();
        }

        var attestationReport = await this.GetAttestationReportSecret(creds, publicKey);
        var signingCert = await this.GetSigningCertSecret(creds, publicKey);

        return new SigningKeyInfo
        {
            AttestationReport = attestationReport,
            SigningCert = signingCert
        };
    }

    public async Task<SigningPrivateKeyInfo> ReleaseSigningKey(string kid)
    {
        var creds = new DefaultAzureCredential();
        string keyName = ToSigningKeyName(kid);

        var jsonWebKey = await this.ReleaseKey(keyName, creds);
        using var ecdaKey = jsonWebKey.ToECDsa(includePrivateParameters: true);

        var publicKey = ecdaKey.ExportSubjectPublicKeyInfoPem();
        var attestationReport = await this.GetAttestationReportSecret(creds, publicKey);
        var signingCert = await this.GetSigningCertSecret(creds, publicKey);
        return new SigningPrivateKeyInfo
        {
            AttestationReport = attestationReport,
            SigningKey = ecdaKey.ExportPkcs8PrivateKeyPem(),
            SigningCert = signingCert
        };
    }

    public async Task<List<(string, IDictionary<string, string>)>> ListEncryptionKeys(string kty)
    {
        var creds = new DefaultAzureCredential();
        var keyClient = new KeyClient(new Uri(this.akvEndpoint), creds);
        List<(string, IDictionary<string, string>)> keyTags = new();
        await foreach (var key in keyClient.GetPropertiesOfKeysAsync())
        {
            if (key.Name.EndsWith("-enc-key"))
            {
                if (key.Tags.TryGetValue(KeyTypeTag, out var ktyValue) &&
                    ktyValue == kty &&
                    key.Tags.TryGetValue(KeyIdTag, out var kid))
                {
                    keyTags.Add((kid, key.Tags));
                }
            }
        }

        return keyTags;
    }

    public async Task<List<(string, IDictionary<string, string>)>> GetKeys(string kty)
    {
        var creds = new DefaultAzureCredential();
        var keyClient = new KeyClient(new Uri(this.akvEndpoint), creds);
        List<(string, IDictionary<string, string>)> keyTags = new();
        await foreach (var key in keyClient.GetPropertiesOfKeysAsync())
        {
            if (key.Tags.TryGetValue(KeyTypeTag, out var ktyValue) &&
                ktyValue == kty &&
                key.Tags.TryGetValue(KeyIdTag, out var value))
            {
                keyTags.Add((value, key.Tags));
            }
        }

        return keyTags.Distinct().ToList();
    }

    private static string FingerprintPublicKey(string publicKey)
    {
        return Attestation.AsReportData(publicKey).ToLower();
    }

    private static string ToEncKeyName(string kid)
    {
        return kid + "-enc-key";
    }

    private static string ToSigningKeyName(string kid)
    {
        return kid + "-signing-key";
    }

    private async Task<JsonWebKey?> GetKey(TokenCredential creds, string keyName)
    {
        var keyClient = new KeyClient(new Uri(this.akvEndpoint), creds);
        try
        {
            KeyVaultKey key = await keyClient.GetKeyAsync(keyName);
            return key.Key;
        }
        catch (RequestFailedException rfe) when (rfe.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task ImportKey(
        TokenCredential creds,
        string keyName,
        string privateKey,
        string hostData,
        KeyType keyType,
        Dictionary<string, string> tags)
    {
        hostData = hostData.ToLower();
        var keyClient = new KeyClient(new Uri(this.akvEndpoint), creds);

        var allOff = new JsonArray
        {
            new JsonObject
            {
                ["claim"] = "x-ms-sevsnpvm-hostdata",
                ["equals"] = hostData
            },
            new JsonObject
            {
                ["claim"] = "x-ms-compliance-status",
                ["equals"] = "azure-compliant-uvm"
            },
            new JsonObject
            {
                ["claim"] = "x-ms-attestation-type",
                ["equals"] = "sevsnpvm"
            }
        };

        var anyOff = new JsonArray
        {
            new JsonObject
            {
                ["authority"] = this.maaEndpoint,
                ["allOf"] = allOff
            }
        };

        var releasePolicy = new JsonObject
        {
            ["version"] = "1.0.0",
            ["anyOf"] = anyOff
        };

        KeyReleasePolicy policy = new(BinaryData.FromString(
                JsonSerializer.Serialize(releasePolicy)))
        {
            Immutable = true
        };

        JsonWebKey keyMaterial;
        if (keyType == KeyType.Rsa)
        {
            using var rsaKey = RSA.Create();
            rsaKey.ImportFromPem(privateKey);
            keyMaterial = new(rsaKey, includePrivateParameters: true);
        }
        else if (keyType == KeyType.Ec)
        {
            using var ecdsaKey = ECDsa.Create();
            ecdsaKey.ImportFromPem(privateKey);
            keyMaterial = new(ecdsaKey, includePrivateParameters: true);
        }
        else
        {
            throw new NotSupportedException($"Unhandled key type {keyType} passed. Fix this.");
        }

        var importKeyOptions = new ImportKeyOptions(keyName, keyMaterial);
        importKeyOptions.HardwareProtected = true;
        importKeyOptions.Properties.Exportable = true;
        importKeyOptions.Properties.ReleasePolicy = policy;

        foreach (var tag in tags)
        {
            importKeyOptions.Properties.Tags.Add(tag.Key, tag.Value);
        }

        await keyClient.ImportKeyAsync(importKeyOptions);
    }

    private async Task<JsonWebKey> ReleaseKey(string keyName, DefaultAzureCredential creds)
    {
        string scope = this.akvEndpoint.ToLower().Contains("vault.azure.net") ?
            "https://vault.azure.net/.default" : "https://managedhsm.azure.net/.default";

        var token = await creds.GetTokenAsync(new TokenRequestContext(new string[] { scope }));

        string uri = $"{this.skrEndpoint}/key/release";
        var skrRequest = new JsonObject
        {
            ["maa_endpoint"] = GetHost(this.maaEndpoint),
            ["akv_endpoint"] = GetHost(this.akvEndpoint),
            ["kid"] = keyName,
            ["access_token"] = token.Token
        };

        static string GetHost(string s)
        {
            if (!s.StartsWith("http"))
            {
                s = "https://" + s;
            }

            return new Uri(s).Host;
        }

        using var skrClient = new HttpClient();
        HttpResponseMessage response = await skrClient.PostAsJsonAsync(uri, skrRequest);
        await response.ValidateStatusCodeAsync(this.logger);
        var skrResponse = await response.Content.ReadFromJsonAsync<JsonObject>();
        var key = skrResponse!["key"]!.ToString();

        var jsonWebKey = JsonSerializer.Deserialize<JsonWebKey>(key)!;
        return jsonWebKey;
    }

    private async Task VerifyKeyImportedByService(string publicKey, AttestationReport report)
    {
        SnpReport snpReport;
        try
        {
            snpReport = SnpReport.VerifySnpAttestation(
                report.Attestation,
                report.PlatformCertificates,
                report.UvmEndorsements);
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

        var trustedServicesPolicy = await this.GetRecoveryServicesHostData();
        if (!trustedServicesPolicy.Any(v => v == snpReport.HostData.ToLower()))
        {
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "HostDataMismatch",
                $"Key imported by service verification failed as attestation report associated " +
                $"with the key " +
                $"has 'hostData' Value '{snpReport.HostData.ToLower()}' which does not " +
                $"match any expected value(s) of the recovery service: " +
                $"{JsonSerializer.Serialize(trustedServicesPolicy)}");
        }

        // A sha256 returns 32 bytes of data while attestation.report_data is 64 bytes
        // (128 chars in a hex string) in size, so need to pad 00s at the end to compare. That is:
        // attestation.report_data = sha256(data)) + 64x(0).
        var publicKeyReportData = Attestation.AsReportData(publicKey);
        var paddedPublicKeyReportData = publicKeyReportData + new string('0', 64);

        if (snpReport.ReportData != paddedPublicKeyReportData)
        {
            throw new ApiException(
                HttpStatusCode.Unauthorized,
                "ReportDataMismatch",
                $"Key imported by service verification failed attestation report associated " +
                $"with the key" +
                $"has 'reportData' Value '{snpReport.ReportData}' which does not match " +
                $"expected value: {paddedPublicKeyReportData} for a key generated by the " +
                $"recovery service.");
        }
    }

    private async Task SetAttestationReportSecret(
        TokenCredential creds,
        string publicKey,
        AttestationReport report)
    {
        var secretName = "report-" + FingerprintPublicKey(publicKey);
        var value = JsonSerializer.Serialize(report);
        var secretClient = new SecretClient(new Uri(this.akvEndpoint), creds);
        await secretClient.SetSecretAsync(new KeyVaultSecret(secretName, value));
    }

    private async Task<AttestationReport> GetAttestationReportSecret(
        TokenCredential creds,
        string publicKey)
    {
        var secretName = "report-" + FingerprintPublicKey(publicKey);
        var secretClient = new SecretClient(new Uri(this.akvEndpoint), creds);
        var secret = await secretClient.GetSecretAsync(secretName);
        var reportValue = secret.Value.Value;
        var attestationReport = JsonSerializer.Deserialize<AttestationReport>(reportValue)!;

        await this.VerifyKeyImportedByService(publicKey, attestationReport);

        return attestationReport;
    }

    private async Task SetSigningCertSecret(
        TokenCredential creds,
        string publicKey,
        string certificate)
    {
        var secretName = "cert-" + FingerprintPublicKey(publicKey);
        var secretClient = new SecretClient(new Uri(this.akvEndpoint), creds);
        await secretClient.SetSecretAsync(new KeyVaultSecret(secretName, certificate));
    }

    private async Task<string> GetSigningCertSecret(
        TokenCredential creds,
        string publicKey)
    {
        var secretName = "cert-" + FingerprintPublicKey(publicKey);
        var secretClient = new SecretClient(new Uri(this.akvEndpoint), creds);
        var secret = await secretClient.GetSecretAsync(secretName);
        return secret.Value.Value;
    }

    private async Task<List<string>> GetRecoveryServicesHostData()
    {
        var hostData = await CcfUtils.GetHostData();
        return [hostData];
    }
}