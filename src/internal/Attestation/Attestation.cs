// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;
using Grpc.Net.Client;
using GrpcAttestationContainerClient;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace AttestationClient;

public class Attestation
{
    public static async Task<AttestationReport> GetReportAsync(byte[] reportData)
    {
        var udsEndPoint = new UnixDomainSocketEndPoint("/mnt/uds/sock");
        var connectionFactory = new UnixDomainSocketsConnectionFactory(udsEndPoint);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                ConnectCallback = connectionFactory.ConnectAsync
            }
        });

        var client = new AttestationContainer.AttestationContainerClient(channel);
        FetchAttestationReply reply = await client.FetchAttestationAsync(new FetchAttestationRequest
        {
            ReportData = ByteString.CopyFrom(reportData)
        });

        return new AttestationReport
        {
            Attestation = Convert.ToBase64String(reply.Attestation.ToByteArray()),
            PlatformCertificates = Convert.ToBase64String(reply.PlatformCertificates.ToByteArray()),
            UvmEndorsements = Convert.ToBase64String(reply.UvmEndorsements.ToByteArray())
        };
    }

    public static async Task<string> GetHostDataAsync()
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

        using var sha256 = SHA256.Create();
        var policy = await File.ReadAllTextAsync(securityPolicyFile);
        var hostData = BitConverter.ToString(
            sha256.ComputeHash(Convert.FromBase64String(policy)))
            .Replace("-", string.Empty)
            .ToLower();
        return hostData;
    }

    public static async Task<AttestationReportKey>
        GenerateRsaKeyPairAndReportAsync()
    {
        using var rsa = RSA.Create(2048);
        string privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
        string publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var reportData = AsReportDataBytes(publicKeyPem);
        var report = await GetReportAsync(reportData);

        return new AttestationReportKey(publicKeyPem, privateKeyPem, report);
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

        var reportData = AsReportDataBytes(publicKeyPem);
        var report = await GetReportAsync(reportData);

        return new AttestationReportKeyCert(certPem, publicKeyPem, privateKeyPem, report);
    }

    public static JsonObject PrepareRequestContent(
        string publicKey,
        AttestationReport attestationReport)
    {
        var content = new JsonObject
        {
            ["encrypt"] = new JsonObject
            {
                ["publicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey))
            }
        };

        content["attestation"] = CcfAttestationReport.ConvertFrom(attestationReport).AsObject();

        return content;
    }

    public static JsonObject PrepareSignedDataRequestContent(
        byte[] data,
        byte[] signature,
        string publicKey,
        AttestationReport attestationReport)
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

        content["attestation"] = CcfAttestationReport.ConvertFrom(attestationReport).AsObject();

        return content;
    }

    public static JsonObject PrepareRequestContent(AttestationReport attestationReport)
    {
        var content = new JsonObject
        {
            ["attestation"] = CcfAttestationReport.ConvertFrom(attestationReport).AsObject()
        };

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

    public static string AsReportData(string publicKey)
    {
        return BitConverter.ToString(AsReportDataBytes(publicKey)).Replace("-", string.Empty);
    }

    public static byte[] AsReportDataBytes(string publicKey)
    {
        using var sha256 = SHA256.Create();
        return sha256.ComputeHash(
            Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(publicKey))));
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
}
