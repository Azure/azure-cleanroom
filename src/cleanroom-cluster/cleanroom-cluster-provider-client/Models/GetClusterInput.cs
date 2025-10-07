// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using CleanRoomProvider;

namespace Controllers;

public class GetClusterInput
{
    public InfraType InfraType { get; set; }

    public JsonObject? ProviderConfig { get; set; }
}