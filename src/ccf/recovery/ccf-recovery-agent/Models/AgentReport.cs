// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AttestationClient;

namespace Controllers;

public class AgentReport
{
    public string Platform { get; set; } = default!;

    public AttestationReport? Report { get; set; } = default!;

    public string ServiceCert { get; set; } = default!;
}