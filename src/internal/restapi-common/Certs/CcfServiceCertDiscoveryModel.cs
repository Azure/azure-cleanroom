// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class CcfServiceCertDiscoveryModel
{
    public string CertificateDiscoveryEndpoint { get; set; } = default!;

    public List<string> HostData { get; set; } = default!;

    public bool SkipDigestCheck { get; set; } = default!;

    public string? ConstitutionDigest { get; set; }

    public string? JsAppBundleDigest { get; set; }
}