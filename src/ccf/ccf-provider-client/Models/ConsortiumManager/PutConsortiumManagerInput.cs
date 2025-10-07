// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace Controllers;

public class PutConsortiumManagerInput
{
    public string InfraType { get; set; } = default!;

    public SecurityPolicyConfigInput? SecurityPolicy { get; set; }

    public JsonObject? ProviderConfig { get; set; }
}