// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;

namespace Controllers;

public class FrontendServiceCertLocator : ServiceCertLocator
{
    public FrontendServiceCertLocator(
        ILogger logger,
        FrontendCertDiscoveryModel model)
        : base(logger, model.CertificateDiscoveryEndpoint, model.HostData)
    {
        this.Model = model;
    }

    public FrontendCertDiscoveryModel Model { get; }

    public override string ExtractCertificate(byte[] reportDataPayloadBytes)
    {
        var contentString = Encoding.UTF8.GetString(reportDataPayloadBytes);
        var reportDataContent =
            JsonSerializer.Deserialize<FrontendServiceCertReportDataContent>(contentString)!;

        if (string.IsNullOrEmpty(reportDataContent?.ServiceCert))
        {
            throw new Exception(
                $"ServiceCertEndpoint did not return any service cert pem: " +
                $"'{contentString}'.");
        }

        return reportDataContent.ServiceCert;
    }
}
