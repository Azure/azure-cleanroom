// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AttestationClient;

namespace Controllers;

public class ServiceCertReport
{
    public string Platform { get; set; } = default!;

    public SnpCACIAttestationReport? Report { get; set; } = default!;

    public string ReportDataPayload { get; set; } = default!;
}