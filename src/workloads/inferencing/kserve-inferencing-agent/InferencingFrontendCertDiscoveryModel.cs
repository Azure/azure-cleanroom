// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class InferencingFrontendCertDiscoveryModel
{
    public string CertificateDiscoveryEndpoint { get; set; } = default!;

    public List<string> HostData { get; set; } = default!;
}