// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AttestationClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CcfTests;

[TestClass]
public class AttestationReportTests
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
    public async Task TestReportParsing()
    {
        var attestationJson = await File.ReadAllTextAsync(
            "insecure-virtual/signing/attestation.json");
        var attestation = JsonSerializer.Deserialize<AttestationReportKeyCert>(attestationJson)!;

        var snpReport = attestation.Report.SnpCaci!;
        var report = SnpReport.VerifySnpAttestation(
            snpReport.Attestation,
            snpReport.PlatformCertificates,
            snpReport.UvmEndorsements);
        var hostData = report.HostData;
        var expectedValue =
            "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20".ToUpper();

        Assert.AreEqual(expectedValue, hostData);

        var publicKeyReportData = Attestation.ToReportDataHashValue(
            Encoding.UTF8.GetBytes(attestation.PublicKey));
        var reportData = report.ReportData;

        // A sha256 returns 32 bytes of data while attestation.report_data is 64 bytes
        // (128 chars in a hex string) in size, so need to pad 00s at the end to compare. That is:
        // attestation.report_data = sha256(data)) + 64x(0).
        var paddedPublicKeyReportData = publicKeyReportData + new string('0', 64);
        Assert.AreEqual(paddedPublicKeyReportData, reportData);
    }
}