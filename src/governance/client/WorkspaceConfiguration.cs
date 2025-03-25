// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class WorkspaceConfiguration
{
    public string SigningCert { get; set; } = default!;

    public string? SigningKey { get; set; } = default!;

    public string? SigningCertId { get; set; } = default!;

    public string CcfEndpoint { get; set; } = default!;

    public string ServiceCert { get; set; } = default!;

    public string MemberId { get; set; } = default!;

    public JsonObject? MemberData { get; set; } = default!;

    public System.Collections.IDictionary EnvironmentVariables { get; set; } = default!;
}
