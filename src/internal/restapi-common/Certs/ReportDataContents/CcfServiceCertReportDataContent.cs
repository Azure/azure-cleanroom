// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public class CcfServiceCertReportDataContent
{
    [JsonPropertyName("serviceCert")]
    public string ServiceCert { get; set; } = default!;

    [JsonPropertyName("constitutionDigest")]
    public string ConstitutionDigest { get; set; } = default!;

    [JsonPropertyName("jsappBundleDigest")]
    public string JsAppBundleDigest { get; set; } = default!;
}
