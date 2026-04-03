// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

/// <summary>
/// Report data content for the frontend service certificate.
/// </summary>
public class FrontendServiceCertReportDataContent
{
    /// <summary>
    /// Gets or sets the certificate in PEM format.
    /// </summary>
    [JsonPropertyName("certificate")]
    public string Certificate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the settings/configuration the frontend is configured with.
    /// </summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, string> Settings { get; set; } = new();
}
