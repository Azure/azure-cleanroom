// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using CcfCommon;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

[assembly: DoNotParallelize]

namespace CcfTests;

[TestClass]
public class AkvTests
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

        this.Logger = loggerFactory.CreateLogger<AkvTests>();
    }

    [TestMethod]
    public async Task TestImportEncryptionKey()
    {
        string akvEndpoint = "https://gsinhakv.vault.azure.net/";
        var akvKeyStore = new AkvKeyStore(
            this.Logger,
            "localhost:8284",
            akvEndpoint,
            "sharedneu.neu.attest.azure.net");
        var memberStore = new MemberStore(akvKeyStore);
        string memberName = Guid.NewGuid().ToString().Substring(0, 8);

        EncryptionKeyInfo firstAttempt = await memberStore.GenerateEncryptionKey(memberName);
        EncryptionKeyInfo secondAttempt = await memberStore.GenerateEncryptionKey(memberName);
        Assert.AreEqual(
            JsonSerializer.Serialize(firstAttempt),
            JsonSerializer.Serialize(secondAttempt));
        Assert.IsNotNull(firstAttempt.AttestationReport);
        Assert.IsFalse(string.IsNullOrEmpty(firstAttempt.EncryptionPublicKey));

        EncryptionKeyInfo? getAttempt = await memberStore.GetEncryptionKey(memberName);
        Assert.IsNotNull(getAttempt);
        Assert.AreEqual(
            JsonSerializer.Serialize(firstAttempt),
            JsonSerializer.Serialize(getAttempt));

        EncryptionPrivateKeyInfo releasedKey = await memberStore.ReleaseEncryptionKey(memberName);
        Assert.IsNotNull(releasedKey.EncryptionPrivateKey);
        EncryptionKeyInfo publicPart = releasedKey;
        Assert.AreEqual(
            JsonSerializer.Serialize(firstAttempt),
            JsonSerializer.Serialize(publicPart));
    }

    [TestMethod]
    public async Task TestImportSigningKey()
    {
        string akvEndpoint = "https://gsinhakv.vault.azure.net/";
        var akvKeyStore = new AkvKeyStore(
            this.Logger,
            "localhost:8284",
            akvEndpoint,
            "sharedneu.neu.attest.azure.net");
        var memberStore = new MemberStore(akvKeyStore);
        string memberName = Guid.NewGuid().ToString().Substring(0, 8);

        SigningKeyInfo firstAttempt = await memberStore.GenerateSigningKey(memberName);
        SigningKeyInfo secondAttempt = await memberStore.GenerateSigningKey(memberName);
        Assert.AreEqual(
            JsonSerializer.Serialize(firstAttempt),
            JsonSerializer.Serialize(secondAttempt));
        Assert.IsNotNull(firstAttempt.AttestationReport);
        Assert.IsFalse(string.IsNullOrEmpty(firstAttempt.SigningCert));

        SigningKeyInfo? getAttempt = await memberStore.GetSigningKey(memberName);
        Assert.IsNotNull(getAttempt);
        Assert.AreEqual(
            JsonSerializer.Serialize(firstAttempt),
            JsonSerializer.Serialize(getAttempt));

        SigningPrivateKeyInfo releasedKey = await memberStore.ReleaseSigningKey(memberName);
        Assert.IsNotNull(releasedKey.SigningKey);
        SigningKeyInfo publicPart = releasedKey;
        Assert.AreEqual(
            JsonSerializer.Serialize(firstAttempt),
            JsonSerializer.Serialize(publicPart));
    }
}