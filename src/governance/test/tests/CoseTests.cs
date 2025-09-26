// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Cose;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using CoseUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class CoseTests
{
    protected IConfiguration Configuration { get; private set; } = default!;

    protected ILogger Logger { get; private set; } = default!;

    /// <summary>
    /// Initialize tests.
    /// </summary>
    [TestInitialize]
    public void Initialize()
    {
        string? testConfigurationFile = Environment.GetEnvironmentVariable(
            "TEST_CONFIGURATION_FILE");

        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json")
            .AddEnvironmentVariables();

        if (!string.IsNullOrEmpty(testConfigurationFile))
        {
            configBuilder.AddJsonFile(testConfigurationFile);
        }

        this.Configuration = configBuilder.Build();

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
        });

        this.Logger = loggerFactory.CreateLogger<CoseTests>();
    }

    [TestMethod]
    public void SignAndVerify()
    {
#pragma warning disable MEN002 // Line is too long
        var signingCert =
            "-----BEGIN CERTIFICATE-----\r\nMIIBtTCCATygAwIBAgIUY9SYtDL/Y4p/a4UNYmZ7knaJz9cwCgYIKoZIzj0EAwMw\r\nEjEQMA4GA1UEAwwHbWVtYmVyMDAeFw0yNDAxMjkxMzAyMzlaFw0yNTAxMjgxMzAy\r\nMzlaMBIxEDAOBgNVBAMMB21lbWJlcjAwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAAQk\r\niEE7/6+NF7C4kRmzeMZz5lbDB+LQlhE/gaipklIZtbkqztDZDTSm2rgKwxNy0XE3\r\nzj1IuZz/RLJAZvHlZRAm96Gq05IXZFamkV7yZlyg9Pi2Hr2jaAsK30UXWOhCvZWj\r\nUzBRMB0GA1UdDgQWBBTJjVBo2fhr0Ie40JMqw9x9KoB35jAfBgNVHSMEGDAWgBTJ\r\njVBo2fhr0Ie40JMqw9x9KoB35jAPBgNVHRMBAf8EBTADAQH/MAoGCCqGSM49BAMD\r\nA2cAMGQCMHOW9fMpRal+3pUU8vwI9y/X91lbIp/3m9Vx24NssbwH94j+k5tyV3aG\r\nz7cDBnG38gIwOGlb7Efd1DjkyjS5pDZNhizEW2CSLBVO+1ZvR1YfRPqLNRMky3G/\r\njt8Wz5K3zJOd\r\n-----END CERTIFICATE-----\r\n";
        var signingKey = "-----BEGIN EC PARAMETERS-----\r\nBgUrgQQAIg==\r\n-----END EC PARAMETERS-----\r\n-----BEGIN EC PRIVATE KEY-----\r\nMIGkAgEBBDA6njOb0k7XmIUtjx3OfSIdW1uHqVFQkEjMhgI+Hi9h+PGTkP9hIugj\r\nMxuIg+TV5fagBwYFK4EEACKhZANiAAQkiEE7/6+NF7C4kRmzeMZz5lbDB+LQlhE/\r\ngaipklIZtbkqztDZDTSm2rgKwxNy0XE3zj1IuZz/RLJAZvHlZRAm96Gq05IXZFam\r\nkV7yZlyg9Pi2Hr2jaAsK30UXWOhCvZU=\r\n-----END EC PRIVATE KEY-----\r\n";
#pragma warning restore MEN002 // Line is too long

        Dictionary<string, string> protectedHeaders = new()
        {
            { "ms.foo", "foo" },
            { "ms.bar", "bar" }
        };

        byte[] message = Cose.Sign(new CoseSignRequest(
            new CoseSignKey(signingCert, signingKey),
            protectedHeaders,
            unprotectedHeaders: null,
            "veryimportantdata"));

        var sign1Message = CoseMessage.DecodeSign1(message);
        Assert.AreEqual(
            protectedHeaders["ms.foo"],
            sign1Message.ProtectedHeaders[new CoseHeaderLabel("ms.foo")].GetValueAsString());
        Assert.AreEqual(
            protectedHeaders["ms.bar"],
            sign1Message.ProtectedHeaders[new CoseHeaderLabel("ms.bar")].GetValueAsString());
        Assert.AreEqual(
            "veryimportantdata",
            Encoding.UTF8.GetString(sign1Message.Content!.Value.Span));
        var result = Cose.Verify(sign1Message, signingCert);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void GenerateKeyAndSignVerify()
    {
        // nistP384 -> secp384r1
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string signingKey = ecdsa.ExportPkcs8PrivateKeyPem();

        var cert = CreateX509Certificate2(ecdsa, "Self-Signed ECDSA");
        string signingCert = cert.ExportCertificatePem();

        Dictionary<string, string> protectedHeaders = new()
        {
            { "ms.foo", "foo" },
            { "ms.bar", "bar" }
        };

        var message = Cose.Sign(new CoseSignRequest(
            new CoseSignKey(signingCert, signingKey),
            protectedHeaders,
            unprotectedHeaders: null,
            "veryimportantdata"));

        var sign1Message = CoseMessage.DecodeSign1(message);
        Assert.AreEqual(
            protectedHeaders["ms.foo"],
            sign1Message.ProtectedHeaders[new CoseHeaderLabel("ms.foo")].GetValueAsString());
        Assert.AreEqual(
            protectedHeaders["ms.bar"],
            sign1Message.ProtectedHeaders[new CoseHeaderLabel("ms.bar")].GetValueAsString());
        Assert.AreEqual(
            "veryimportantdata",
            Encoding.UTF8.GetString(sign1Message.Content!.Value.Span));
        var result = Cose.Verify(sign1Message, signingCert);
        Assert.IsTrue(result);
    }

    //[TestMethod]
    public async Task GenerateKeyVaultCertAndSignVerify()
    {
        string akvEndpoint = "https://gsinhakv.vault.azure.net/";
        var creds = new DefaultAzureCredential();
        var certName = "mcert-cose-test";
        var certClient = new CertificateClient(new Uri(akvEndpoint), creds);
        X509Certificate2 certificate;

        try
        {
            certificate = await certClient.DownloadCertificateAsync(certName);
        }
        catch (RequestFailedException rfe) when (rfe.ErrorCode == "CertificateNotFound")
        {
            // https://microsoft.github.io/CCF/main/governance/hsm_keys.html#certificate-and-key-generation
            CertificateOperation createOperation = await certClient.StartCreateCertificateAsync(
                certName,
                new CertificatePolicy(issuerName: "Self", subject: "CN=Member")
                {
                    ContentType = CertificateContentType.Pkcs12,
                    KeyType = CertificateKeyType.Ec,
                    KeyCurveName = CertificateKeyCurveName.P384,
                    Exportable = true,
                    ReuseKey = true,
                    KeyUsage = { CertificateKeyUsage.DigitalSignature },
                    ValidityInMonths = 12,
                    LifetimeActions =
                    {
                        new LifetimeAction(CertificatePolicyAction.AutoRenew)
                        {
                            DaysBeforeExpiry = 90,
                        }
                    },
                });
            await createOperation.WaitForCompletionAsync(
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            certificate = await certClient.DownloadCertificateAsync(certName);
        }

        Dictionary<string, string> protectedHeaders = new()
        {
            { "ms.foo", "foo" },
            { "ms.bar", "bar" }
        };

        var coseSignKey = new CoseSignKey(new Uri($"{akvEndpoint}/{certName}"), certificate, creds);
        var message = Cose.Sign(new CoseSignRequest(
            coseSignKey,
            protectedHeaders,
            unprotectedHeaders: null,
            "veryimportantdata"));

        var sign1Message = CoseMessage.DecodeSign1(message);
        Assert.AreEqual(
            protectedHeaders["ms.foo"],
            sign1Message.ProtectedHeaders[new CoseHeaderLabel("ms.foo")].GetValueAsString());
        Assert.AreEqual(
            protectedHeaders["ms.bar"],
            sign1Message.ProtectedHeaders[new CoseHeaderLabel("ms.bar")].GetValueAsString());
        Assert.AreEqual(
            "veryimportantdata",
            Encoding.UTF8.GetString(sign1Message.Content!.Value.Span));
        var result = Cose.Verify(sign1Message, coseSignKey.Certificate);
        Assert.IsTrue(result);
    }

    private static X509Certificate2 CreateX509Certificate2(ECDsa key, string certName)
    {
        var req = new CertificateRequest($"cn={certName}", key, HashAlgorithmName.SHA256);
        var cert = req.CreateSelfSigned(DateTimeOffset.Now, DateTimeOffset.Now.AddYears(1));
        return cert;
    }
}