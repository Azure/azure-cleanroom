// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using AttestationClient;

namespace FrontendSvc.Models;

public class ConsortiumManagerReport
{
    public required string Platform { get; set; }

    public required SnpCACIAttestationReport? Report { get; set; }

    public required string ServiceCert { get; set; }

    public required string? HostData { get; set; }
}