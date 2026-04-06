// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace FrontendSvc;

public class WorkspaceConfiguration
{
    public string ConsortiumManagerEndpoint { get; set; } = default!;

    public string MembershipManagerEndpoint { get; set; } = default!;

    public string CgsClientEndpoint { get; set; } = default!;

    public string TenantId { get; set; } = default!;

    public string AnalyticsWorkloadId { get; set; } = default!;

    public string FirstPartyAppId { get; set; } = default!;

    public string KeyVaultUrl { get; set; } = default!;

    public string FirstPartyAppCertificateName { get; set; } = default!;

    public string? FirstPartyAppTokenScope { get; set; }

    public string CcrServiceCertPath { get; set; } = default!;
}