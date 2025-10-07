// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Controllers;

public class CcfServiceCertLocator : ServiceCertLocator
{
    public CcfServiceCertLocator(ILogger logger, CcfServiceCertDiscoveryModel model)
        : base(logger, model.CertificateDiscoveryEndpoint, model.HostData)
    {
        this.Model = model;
    }

    public CcfServiceCertDiscoveryModel Model { get; }

    public override string ExtractCertificate(byte[] reportDataPayloadBytes)
    {
        var contentString = Encoding.UTF8.GetString(reportDataPayloadBytes);
        var reportDataContent = JsonSerializer.Deserialize<CcfServiceCertReportDataContent>(
            contentString)!;

        if (this.Model.SkipDigestCheck)
        {
            this.Logger.LogWarning($"Skipping constitution digest " +
                $"({reportDataContent.ConstitutionDigest}) check as skipDigestCheck was " +
                $"was specified.");
        }
        else if (this.Model.ConstitutionDigest != reportDataContent.ConstitutionDigest)
        {
            throw new Exception(
                $"ConstitutionDigestMismatch: Attestation report constitution digest value did " +
                $"not match expected constitution digest value. " +
                $"Report value: {reportDataContent.ConstitutionDigest}, " +
                $"expected value: {this.Model.ConstitutionDigest}.");
        }

        if (this.Model.SkipDigestCheck)
        {
            this.Logger.LogWarning($"Skipping jsapp bundle digest " +
                $"({reportDataContent.JsAppBundleDigest}) check as skipDigestCheck was " +
                $"specified.");
        }
        else if (this.Model.JsAppBundleDigest != reportDataContent.JsAppBundleDigest)
        {
            throw new Exception(
                $"JsAppBundleDigestMismatch: Attestation report jsapp bundle digest value did " +
                $"not match expected jsapp bundle digest value. " +
                $"Report value: {reportDataContent.JsAppBundleDigest}, " +
                $"expected value: {this.Model.JsAppBundleDigest}.");
        }

        if (string.IsNullOrEmpty(reportDataContent?.ServiceCert))
        {
            throw new Exception(
                $"ServiceCertEndpoint did not return any service cert pem: " +
                $"'{contentString}'.");
        }

        return reportDataContent.ServiceCert;
    }
}