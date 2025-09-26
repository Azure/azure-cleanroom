// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AttestationClient;

namespace Controllers;

public class WorkspaceConfiguration
{
    public string CcrgovEndpoint { get; set; } = default!;

    public string? ServiceCert { get; set; } = default!;

    public CcfServiceCertLocator? ServiceCertLocator { get; set; } = default!;

    public AttestationReportKey Attestation { get; set; } = default!;
}