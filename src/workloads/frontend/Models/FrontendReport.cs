// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AttestationClient;

namespace FrontendSvc.Models;

/// <summary>
/// Response model for the /report endpoint.
/// </summary>
public class FrontendReport
{
    public string Platform { get; set; } = default!;

    public SnpCACIAttestationReport? Report { get; set; } = default!;

    public string ReportDataPayload { get; set; } = default!;
}
