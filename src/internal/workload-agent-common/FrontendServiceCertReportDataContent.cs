// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Controllers;

public class FrontendServiceCertReportDataContent
{
    [JsonPropertyName("serviceCert")]
    public string ServiceCert { get; set; } = default!;
}
