// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AttestationClient;
using Microsoft.Extensions.Logging;

namespace Controllers;

public abstract class ServiceCertLocator
{
    private const string AllowAll =
        "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";

    public ServiceCertLocator(
        ILogger logger,
        string certificateDiscoveryEndpoint,
        List<string> hostData)
    {
        if (string.IsNullOrEmpty(certificateDiscoveryEndpoint))
        {
            throw new ArgumentException("certificateDiscoveryEndpoint must be specified");
        }

        if (hostData == null || hostData.Count == 0)
        {
            throw new ArgumentException("hostData must be specified");
        }

        this.Logger = logger;
        this.CertificateDiscoveryEndpoint = certificateDiscoveryEndpoint;
        this.HostData = hostData;
    }

    public ILogger Logger { get; }

    public string CertificateDiscoveryEndpoint { get; }

    public List<string> HostData { get; }

    public abstract string ExtractCertificate(byte[] reportDataPayloadBytes);

    public async Task<string> DownloadServiceCertificatePem()
    {
        var uri = new Uri(this.CertificateDiscoveryEndpoint!);
        string baseAddress = uri.GetLeftPart(UriPartial.Authority);

        var client = HttpClientManager.NewInsecureClient(
            baseAddress,
            this.Logger,
            HttpRetries.Policies.DefaultRetryPolicy(this.Logger));
        var serviceCertReport = await client.GetFromJsonAsync<ServiceCertReport>(uri.AbsolutePath);
        if (serviceCertReport == null)
        {
            throw new Exception($"CertificateReportUrl did not return any data.");
        }

        var attestationReport = serviceCertReport.Report;
        var reportDataPayloadBytes = Convert.FromBase64String(serviceCertReport.ReportDataPayload);
        if (attestationReport == null &&
            this.HostData.Count == 1 &&
            this.HostData[0] == AllowAll)
        {
            // Skip validation if no report was presented and the expected hostData was also an
            // allow all (insecure) policy.
            this.Logger.LogInformation(
                "Skipping report data content validation as no attestation report was returned " +
                $"by {this.CertificateDiscoveryEndpoint} endpoint and allow all " +
                "policy is in use.");
        }
        else if (attestationReport == null)
        {
            throw new Exception(
                $"ServiceCertEndpoint did not return any attestation report: " +
                $"'{JsonSerializer.Serialize(serviceCertReport)}'.");
        }
        else
        {
            var snpReport = SnpReport.VerifySnpAttestation(
                attestationReport.Attestation,
                attestationReport.PlatformCertificates,
                attestationReport.UvmEndorsements);
            if (!this.HostData.Any(v => v.ToUpper() == snpReport.HostData))
            {
                throw new Exception(
                    $"HostDataMismatch: Attestation report host data value did not match " +
                    $"expected host data value(s). " +
                    $"Report value: {snpReport.HostData.ToLower()}, " +
                    $"expected value(s): {JsonSerializer.Serialize(this.HostData)}.");
            }

            if (string.IsNullOrEmpty(serviceCertReport.ReportDataPayload))
            {
                throw new Exception(
                    $"ServiceCertEndpoint did not return any report payload: " +
                    $"'{JsonSerializer.Serialize(serviceCertReport)}'.");
            }

            var hash = SHA256.HashData(reportDataPayloadBytes);
            var expectedReportData = BitConverter.ToString(hash).Replace("-", string.Empty);

            // A sha256 returns 32 bytes of data while attestation.report_data is 64 bytes
            // (128 chars in a hex string) in size, so need to pad 00s at the end to compare.
            // That is:
            // attestation.report_data = sha256(data)) + 64x(0).
            expectedReportData += new string('0', 64);
            if (snpReport.ReportData != expectedReportData)
            {
                throw new Exception(
                    $"ReportDataMismatch: Attestation report report data value did not match " +
                    $"expected report data value(s). " +
                    $"Report value: {snpReport.ReportData}, " +
                    $"expected value: {expectedReportData}.");
            }
        }

        var serviceCertPem = this.ExtractCertificate(reportDataPayloadBytes);

        this.Logger.LogInformation($"Downloaded service certificate from " +
            $"{this.CertificateDiscoveryEndpoint}: {serviceCertPem}");

        return serviceCertPem;
    }
}