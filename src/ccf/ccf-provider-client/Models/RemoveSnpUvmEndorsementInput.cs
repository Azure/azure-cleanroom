// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class RemoveSnpUvmEndorsementInput
{
    public string InfraType { get; set; } = default!;

    /// <summary>
    /// Gets or sets the DID (Decentralized Identifier) for the UVM endorsement issuer.
    /// </summary>
    public string Did { get; set; } = default!;

    /// <summary>
    /// Gets or sets the feed name for the UVM endorsement to remove.
    /// </summary>
    public string Feed { get; set; } = default!;

    public JsonObject? ProviderConfig { get; set; }
}
