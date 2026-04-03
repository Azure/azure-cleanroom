// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class AddSnpUvmEndorsementInput
{
    public string InfraType { get; set; } = default!;

    /// <summary>
    /// Gets or sets the DID (Decentralized Identifier) for the UVM endorsement issuer.
    /// </summary>
    public string Did { get; set; } = default!;

    /// <summary>
    /// Gets or sets the feed name for the UVM endorsement (e.g. "ContainerPlat-AMD-UVM").
    /// </summary>
    public string Feed { get; set; } = default!;

    /// <summary>
    /// Gets or sets the minimum SVN (Security Version Number) as a string.
    /// </summary>
    public string Svn { get; set; } = default!;

    public JsonObject? ProviderConfig { get; set; }
}
