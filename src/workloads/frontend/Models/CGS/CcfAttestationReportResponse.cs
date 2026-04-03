// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using AttestationClient;

namespace FrontendSvc.Models;

public class CcfAttestationReportResponse
{
    public required string Platform { get; set; }

    public SnpCACIAttestationReport? Report { get; set; }

    public required string ReportDataPayload { get; set; }
}

public class ReportDataContent
{
    public required string ServiceCert { get; set; }

    public string? ConstitutionDigest { get; set; }

    public string? JsAppBundleDigest { get; set; }
}