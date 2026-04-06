// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AttestationClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Test;

[TestClass]
public class EncryptionTests
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
    public void WrapUnWrapRsaOaepAesKwpValue()
    {
        using var rsa = RSA.Create(2048);
        string payload = new('*', 5000);
        var input = Encoding.UTF8.GetBytes(payload);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();

        // Do a encode/decode logic to verify how the public key gets transported across is ok.
        var v = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem()));
        var decodedPublicKey = Encoding.UTF8.GetString(Convert.FromBase64String(v));
        Assert.AreEqual(publicKey, decodedPublicKey);

        var wrappedValue = Attestation.WrapRsaOaepAesKwpValue(
            input,
            rsa.ExportSubjectPublicKeyInfoPem());
        var unwrappedValue = Attestation.UnwrapRsaOaepAesKwpValue(
            wrappedValue,
            rsa.ExportPkcs8PrivateKeyPem());
        Assert.AreEqual(
            payload,
            Encoding.UTF8.GetString(unwrappedValue));
    }

    [TestMethod]
    public void SignAndVerify()
    {
        using var rsa = RSA.Create(2048);
        string payload = new('*', 5000);
        var input = Encoding.UTF8.GetBytes(payload);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var privateKey = rsa.ExportPkcs8PrivateKeyPem();

        var signature = Signing.SignData(input, privateKey, RSASignaturePaddingMode.Pss);
        var result =
            Signing.VerifyDataUsingKey(input, signature, publicKey, RSASignaturePaddingMode.Pss);
        Assert.IsTrue(result);
    }
}