// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.Cose;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Tokens;

namespace AttestationClient;

public class UvmCoseSign1Message
{
    // https://github.com/microsoft/did-x509/blob/main/specification.md
    private const string TrustedMicrosoftIssValue =
#pragma warning disable MEN002 // Line is too long
        "did:x509:0:sha256:I__iuL25oXEVFdTP_aBLx_eT1RPHbCQ_ECBQfYZpt9s::eku:1.3.6.1.4.1.311.76.59.1.2";
#pragma warning restore MEN002 // Line is too long

    private const string TrustedMicrosoftFeedValue = "ContainerPlat-AMD-UVM";

    private const string EkuExtensionOidValue = "2.5.29.37";

    private CoseSign1Message message;

    // See header values for algorithms here: https://www.iana.org/assignments/cose/cose.xhtml
    private Dictionary<int, string> supportedAlgorithmsByHeaderValue = new()
    {
        { -259, "RSA" },
        { -258, "RSA" },
        { -257, "RSA" },
        { -39,  "RSA" },
        { -38, "RSA" },
        { -37, "RSA" },
        { -36, "ECDsa" },
        { -35, "ECDsa" },
        { -7, "ECDsa" },
    };

    public UvmCoseSign1Message(CoseSign1Message message)
    {
        this.message = message;
    }

    public byte[] GetRawContent()
    {
        if (!this.message.Content.HasValue)
        {
            throw new Exception("Uvm endorsement cose message content is empty.");
        }

        return this.message.Content.Value.ToArray();
    }

    public UvmPayload GetUvmPayload()
    {
        var content = this.GetRawContent();
        UvmPayload? result;
        try
        {
            result = JsonSerializer.Deserialize<UvmPayload>(Encoding.UTF8.GetString(content));
        }
        catch (JsonException ex)
        {
            throw new Exception($"Uvm payload deserialization failed: {ex.Message}", ex);
        }

        if (result == null)
        {
            throw new Exception("Got empty UvmPayload.");
        }

        return result;
    }

    public void Verify()
    {
        try
        {
            // Logic as per:
            // https://github.com/microsoft/confidential-aci-examples/blob/main/docs/Confidential_ACI_SCHEME.md#reference-info-base64
            var iss = this.message.ProtectedHeaders.GetValueAsString(new CoseHeaderLabel("iss"));
            var feed = this.message.ProtectedHeaders.GetValueAsString(new CoseHeaderLabel("feed"));

            if (iss != TrustedMicrosoftIssValue)
            {
                throw new Exception($"Unexpected iss value of {iss}. " +
                    $"Expected value was {TrustedMicrosoftIssValue}.");
            }

            if (feed != TrustedMicrosoftFeedValue)
            {
                throw new Exception($"Unexpected feed value of {feed}. " +
                    $"Expected value was {TrustedMicrosoftFeedValue}.");
            }

            // Check cert chain is valid.
            // Reference code:
            // https://github.com/microsoft/cosesign1go/blob/e86c6aa1092c0386c91f3b24c3111a03be0094b6/pkg/cosesign1/check.go#L106
            var x5ChainLabel = new CoseHeaderLabel(33);
            if (!this.message.ProtectedHeaders.TryGetValue(
                x5ChainLabel,
                out CoseHeaderValue x5chain))
            {
                throw new Exception("x5chain missing in uvm cose sign1 message.");
            }

            var certCollection = new X509Certificate2Collection();
            var reader = new CborReader(x5chain.EncodedValue);
            if (reader.PeekState() == CborReaderState.StartArray)
            {
                int? arrayLength = reader.ReadStartArray();
                if (arrayLength == null || arrayLength.Value > 100)
                {
                    throw new Exception(
                        $"Unreasonable number of certs '{arrayLength.GetValueOrDefault()}' in " +
                        $"COSE_Sign1 document.");
                }

                for (int i = 0; i < arrayLength; i++)
                {
                    var cert = X509CertificateLoader.LoadCertificate(reader.ReadByteString());
                    certCollection.Add(cert);
                }
            }
            else if (reader.PeekState() == CborReaderState.ByteString)
            {
                var cert = X509CertificateLoader.LoadCertificate(reader.ReadByteString());
                certCollection.Add(cert);
            }
            else
            {
                throw new Exception($"Unexpected x5chain type {reader.PeekState()}.");
            }

            // Example certCollection:
            // [0] CN=ContainerPlat->Microsoft SCD Products RSA CA
            // [1] Microsoft SCD Products RSA CA->Microsoft Supply Chain RSA Root CA 2022
            // [2] Microsoft Supply Chain RSA Root CA 2022->Microsoft Supply Chain RSA Root CA 2022
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags =
                    X509VerificationFlags.AllowUnknownCertificateAuthority |
                    X509VerificationFlags.IgnoreNotTimeValid;

                var parsedRootCert = certCollection[certCollection.Count - 1];
                var parsedLeafCert = certCollection[0];
                foreach (var cert in certCollection.Reverse())
                {
                    if (!chain.Build(cert))
                    {
                        StringBuilder sb =
                            new($"'{cert.Subject}' certificate chain was not valid.");
                        if (chain.ChainStatus.Length > 0)
                        {
                            sb.Append(
                                $" chainStatus: {JsonSerializer.Serialize(chain.ChainStatus)}");
                        }

                        throw new Exception(sb.ToString());
                    }

                    chain.ChainPolicy.CustomTrustStore.Add(cert);
                }
            }

            var leafCert = certCollection[0];
            var rootCert = certCollection[certCollection.Count - 1];

            // Validate that the root cert is the one mentioned in the issuer did:x509 value.
            var didTokens = TrustedMicrosoftIssValue.Split(":");
            if (didTokens[3] != "sha256")
            {
                throw new Exception($"Unsupported hash algo type {didTokens[3]} in did:X509.");
            }

            var value = Base64UrlEncoder.Encode(rootCert.GetCertHash(HashAlgorithmName.SHA256));
            var expectedRoot = didTokens[4];
            if (value != expectedRoot)
            {
                throw new Exception(
                    $"unexpected certificate fingerprint '{value}' when " +
                    $"expecting '{expectedRoot}'.");
            }

            // Validate that the leaf cert is having the expected eku.
            X509EnhancedKeyUsageExtension? ekuExtension = null;
            foreach (X509Extension extension in leafCert.Extensions)
            {
                if (extension.Oid?.Value == EkuExtensionOidValue)
                {
                    ekuExtension = (X509EnhancedKeyUsageExtension?)extension;
                    break;
                }
            }

            if (ekuExtension == null)
            {
                throw new Exception("No EKU extension in leaf cert.");
            }

            var ekuPolicy = TrustedMicrosoftIssValue.Split("::")[1].Split(":")[1];
            var foundExpectedOid = false;
            foreach (var ekuOid in ekuExtension.EnhancedKeyUsages)
            {
                if (ekuOid.Value == ekuPolicy)
                {
                    foundExpectedOid = true;
                    break;
                }
            }

            if (!foundExpectedOid)
            {
                throw new Exception($"Expected EKU {ekuPolicy} not found on leaf cert.");
            }

            // Validate message is signed by the leaf  cert.
            AsymmetricAlgorithm? alg = null;
            var algHeader = this.message.ProtectedHeaders.GetValueAsInt32(CoseHeaderLabel.Algorithm);
            var algString = this.supportedAlgorithmsByHeaderValue.GetValueOrDefault(algHeader);
            switch (algString)
            {
                case "RSA":
                    alg = leafCert.GetRSAPublicKey();
                    break;
                case "ECDsa":
                    alg = leafCert.GetECDsaPublicKey();
                    break;
                default:
                    throw new Exception(
                        $"Unsupported signing algorithm by header value: {algHeader} {algString}");
            }

            if (alg == null)
            {
                throw new Exception(
                    $"Expected to find public key of type {algString} but found none.");
            }

            if (!this.message.VerifyEmbedded(alg))
            {
                throw new Exception("VerifyEmbedded failed for uvm cose sign1 message.");
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Verification of UVM endorsement failed: {ex.Message}.", ex);
        }
    }

    public class UvmPayload
    {
        public UvmPayload(string launchMeasurement)
        {
            this.LaunchMeasurement = launchMeasurement;
        }

        [JsonPropertyName("x-ms-sevsnpvm-launchmeasurement")]
        public string LaunchMeasurement { get; set; }
    }
}