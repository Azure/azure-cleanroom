// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AttestationClient;

namespace Controllers;

public class WorkspaceConfiguration
{
    public string CcrgovEndpoint { get; set; } = default!;

    public string? ServiceCert { get; set; } = default!;

    public CcfServiceCertLocator? ServiceCertLocator { get; set; } = default!;

    public KeyPair KeyPair { get; set; } = default!;

    public AttestationReport? Report { get; set; }

    public JwtTokenConfiguration? JwtTokenConfiguration { get; set; }

    public string? AuthMode { get; set; } = default!;
}