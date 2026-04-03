// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AttestationClient;

namespace Controllers;

public class RecoveryServiceReport
{
    public string Platform { get; set; } = default!;

    public SnpCACIAttestationReport? Report { get; set; } = default!;

    public string ServiceCert { get; set; } = default!;

    public string? HostData { get; set; } = default!;
}