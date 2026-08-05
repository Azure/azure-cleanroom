// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CleanRoomProvider;

namespace Controllers;

public class CreateFlexNodeInput
{
    public InfraType InfraType { get; set; }

    public string ProviderID { get; set; } = default!;

    public string PolicySigningCertPem { get; set; } = default!;

    public JsonObject? ProviderConfig { get; set; }
}
