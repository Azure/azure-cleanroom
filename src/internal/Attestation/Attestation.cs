// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace AttestationClient;

public class Attestation
{
    private const string AllowAllHostData =
        "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";

    // Fixed 32-byte nonce (SHA-256 of "azure-cleanroom") used for TPM quotes.
    private static readonly string TpmQuoteNonce =
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("azure-cleanroom")));

    private static readonly HttpClient SkrClient = new()
    {
        BaseAddress = new Uri(
            $"http://localhost:{Environment.GetEnvironmentVariable("SKR_PORT") ?? "8284"}")
    };

    private static readonly HttpClient CvmAttestationAgentClient = new()
    {
        BaseAddress = new Uri(
            $"http://localhost:{Environment.GetEnvironmentVariable("CVM_ATTESTATION_AGENT_PORT")
                ?? "8900"}")
    };

    private static readonly bool IsUvmSecurityContextDirPresent =
     !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UVM_SECURITY_CONTEXT_DIR"));

    public static string ToReportDataHashValue(byte[] reportData)
    {
        return BitConverter.ToString(SHA256.HashData(reportData)).Replace("-", string.Empty);
    }

    public static bool IsSnpCACI()
    {
        if (IsVirtualEnvironment())
        {
            return false;
        }

        if (!IsUvmSecurityContextDirPresent)
        {
            throw new Exception("UVM_SECURITY_CONTEXT_DIR is not set. Are you running in CACI?");
        }

        return true;

        static bool IsVirtualEnvironment()
        {
            return Environment.GetEnvironmentVariable("INSECURE_VIRTUAL_ENVIRONMENT") == "true";
        }
    }

    public static async Task<string> GetCACIHostData()
    {
        if (IsSnpCACI())
        {
            var hostData = await GetCACIHostDataAsync();
            return hostData;
        }
        else
        {
            return AllowAllHostData;
        }
    }

    public static KeyPair GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        string privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        string publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
        return new KeyPair(publicKeyPem, privateKeyPem);
    }

    public static async Task<AttestationReportKey>
        GenerateRsaKeyPairAndReportAsync()
    {
        var keyPair = GenerateRsaKeyPair();

        var reportData = Encoding.UTF8.GetBytes(keyPair.PublicKey);
        AttestationReport report = IsUvmSecurityContextDirPresent ?
            await GetCACIReportAsync(reportData) : await GetCvmReportAsync(reportData);

        return new AttestationReportKey(keyPair.PublicKey, keyPair.PrivateKey, report);
    }

    public static async Task<AttestationReportKeyCert>
        GenerateEcdsaKeyPairAndReportAsync()
    {
        // nistP384 -> secp384r1
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string privateKeyPem = ecdsa.ExportPkcs8PrivateKeyPem();

        var cert = CreateX509Certificate2(ecdsa, "Self-Signed ECDSA");
        string certPem = cert.ExportCertificatePem();
        string publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        var reportData = Encoding.UTF8.GetBytes(publicKeyPem);
        AttestationReport report = IsUvmSecurityContextDirPresent ?
            await GetCACIReportAsync(reportData) : await GetCvmReportAsync(reportData);

        return new AttestationReportKeyCert(certPem, publicKeyPem, privateKeyPem, report);
    }

    public static JsonObject PrepareRequestContent(
        string publicKey,
        AttestationReport? attestationReport)
    {
        var content = new JsonObject
        {
            ["encrypt"] = new JsonObject
            {
                ["publicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey))
            }
        };

        if (attestationReport != null)
        {
            AddAttestationToContent(content, attestationReport);
        }

        return content;
    }

    public static JsonObject PrepareSignedDataRequestContent(
        byte[] data,
        byte[] signature,
        string publicKey,
        AttestationReport? attestationReport)
    {
        var content = new JsonObject
        {
            ["data"] = Convert.ToBase64String(data),
            ["timestamp"] = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeMilliseconds().ToString(),
            ["sign"] = new JsonObject
            {
                ["signature"] = Convert.ToBase64String(signature),
                ["publicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey))
            }
        };

        content["encrypt"] = new JsonObject
        {
            ["publicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey))
        };

        if (attestationReport != null)
        {
            AddAttestationToContent(content, attestationReport);
        }

        return content;
    }

    public static JsonObject PrepareRequestContent(AttestationReport? attestationReport)
    {
        var content = new JsonObject();
        if (attestationReport != null)
        {
            AddAttestationToContent(content, attestationReport);
        }

        return content;
    }

    public static byte[] WrapRsaOaepAesKwpValue(byte[] value, string publicKeyPem)
    {
        // Do the equivalent of ccf.crypto.wrapKey(algo: RSA-OAEP-AES-KWP) using BouncyCastle.
        // Note (gsinha): Using BouncyCastle as not able to figure out how to do the equivalent
        // with .NET libraries.
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(publicKeyPem);

        using var encryptionKey = Aes.Create();
        encryptionKey.KeySize = 256;
        encryptionKey.GenerateKey();
        byte[] aesKey = encryptionKey.Key;
        byte[] firstPart = publicKey.Encrypt(aesKey, RSAEncryptionPadding.OaepSHA256);
        if (firstPart.Length != 256)
        {
            throw new Exception(
                $"Incorrect understanding of expected encrypted output length. " +
                $"Expected: 256, Actual: {firstPart.Length}.");
        }

        var en = new AesWrapPadEngine();
        en.Init(true, new KeyParameter(aesKey));
        var secondPart = en.Wrap(value, 0, value.Length);

        return firstPart.Concat(secondPart).ToArray();
    }

    public static byte[] UnwrapRsaOaepAesKwpValue(byte[] wrappedValue, string privateKeyPem)
    {
        // Do the equivalent of ccf.crypto.unwrapKey(algo: RSA-OAEP-AES-KWP) using BouncyCastle.
        // Note (gsinha): Using BouncyCastle as not able to figure out how to do the equivalent
        // with .NET libraries.
        using var privateKey = RSA.Create();
        var firstPart = wrappedValue.Take(256).ToArray();
        var secondPart = wrappedValue.Skip(256).ToArray();
        privateKey.ImportFromPem(privateKeyPem);
        var aesKey = privateKey.Decrypt(firstPart, RSAEncryptionPadding.OaepSHA256);
        var en = new AesWrapPadEngine();
        en.Init(false, new KeyParameter(aesKey));
        var unwrappedValue = en.Unwrap(secondPart, 0, secondPart.Length);
        return unwrappedValue;
    }

    public static async Task<AttestationReport> GetCACIReportAsync(byte[] reportData)
    {
        using var response = await SkrClient.PostAsync("/attest/combined", JsonContent.Create(new
        {
            runtime_data = Convert.ToBase64String(reportData)
        }));
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new Azure.RequestFailedException((int)response.StatusCode, content);
        }

        var combinedReport = (await response.Content.ReadFromJsonAsync<CombinedReport>())!;
        return new AttestationReport
        {
            SnpCaci = new SnpCACIAttestationReport
            {
                Attestation = combinedReport.Evidence,
                PlatformCertificates = combinedReport.Endorsements,
                UvmEndorsements = combinedReport.UvmEndorsements
            }
        };
    }

    private static void AddAttestationToContent(
        JsonObject content,
        AttestationReport attestationReport)
    {
        if (attestationReport.SnpCaci != null && attestationReport.SnpCvm != null)
        {
            throw new ArgumentException("Attestation report contains multiple report types.");
        }

        if (attestationReport.SnpCaci != null)
        {
            content["platform"] = "snp-caci";
            content["attestation"] =
                CcfSnpCACIAttestationReport.ConvertFrom(attestationReport.SnpCaci).AsObject();
        }
        else if (attestationReport.SnpCvm != null)
        {
            content["platform"] = "snp-cvm";
            var attestationObject =
                CcfSnpCvmAttestationReport.ConvertFrom(attestationReport.SnpCvm).AsObject();

            // runtimeClaims are not sent over as any verifier should extract it from the HCL
            //  report blob post verification and rely on that.
            attestationObject.Remove("runtimeClaims");
            content["attestation"] = attestationObject;
        }
        else
        {
            throw new ArgumentException(
                $"Unsupported attestation report type: " +
                $"{attestationReport.GetType().FullName}");
        }
    }

    private static async Task<AttestationReport> GetCvmReportAsync(byte[] reportData)
    {
        using var response = await CvmAttestationAgentClient.PostAsync(
            "/snp/attest",
            JsonContent.Create(new
            {
                nonce = TpmQuoteNonce,

                // SNP report_data is 64 bytes: SHA-256(reportData) (32 bytes) + 32 zero bytes.
                reportData = Convert.ToBase64String(
                    SHA256.HashData(reportData).Concat(new byte[32]).ToArray())
            }));
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new Azure.RequestFailedException((int)response.StatusCode, content);
        }

        var report = await response.Content.ReadFromJsonAsync<SnpCvmAttestationReport>();
        return new AttestationReport
        {
            SnpCvm = report
        };
    }

    private static async Task<string> GetCACIHostDataAsync()
    {
        var securityContextDir = Environment.GetEnvironmentVariable("UVM_SECURITY_CONTEXT_DIR");
        if (string.IsNullOrEmpty(securityContextDir))
        {
            throw new Exception("UVM_SECURITY_CONTEXT_DIR is not set.");
        }

        var securityPolicyFile = Path.Combine(securityContextDir, "security-policy-base64");
        if (!Path.Exists(securityPolicyFile))
        {
            throw new Exception($"{securityPolicyFile} is not present.");
        }

        var policy = await File.ReadAllTextAsync(securityPolicyFile);
        var hostData = BitConverter.ToString(SHA256.HashData(Convert.FromBase64String(policy)))
            .Replace("-", string.Empty)
            .ToLower();
        return hostData;
    }

    private static X509Certificate2 CreateX509Certificate2(ECDsa key, string certName)
    {
        var req = new CertificateRequest($"cn={certName}", key, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
        return cert;
    }

    internal class UnixDomainSocketsConnectionFactory
    {
        private readonly EndPoint endPoint;

        public UnixDomainSocketsConnectionFactory(EndPoint endPoint)
        {
            this.endPoint = endPoint;
        }

        public async ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext x,
            CancellationToken cancellationToken = default)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                await socket.ConnectAsync(this.endPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    internal record CombinedReport(
        [property: JsonPropertyName("endorsed_tcb")] string EndorsedTcb,
        [property: JsonPropertyName("endorsements")] string Endorsements,
        [property: JsonPropertyName("evidence")] string Evidence,
        [property: JsonPropertyName("uvm_endorsements")] string UvmEndorsements);
}
