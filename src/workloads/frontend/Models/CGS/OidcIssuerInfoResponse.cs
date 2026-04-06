// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class OidcIssuerInfoResponse
{
    public bool Enabled { get; set; }

    public string? IssuerUrl { get; set; }

    public TenantData? TenantData { get; set; }
}

public class TenantData
{
    public string TenantId { get; set; } = default!;

    public string IssuerUrl { get; set; } = default!;
}