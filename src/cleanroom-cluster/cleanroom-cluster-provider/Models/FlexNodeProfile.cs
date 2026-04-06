// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace CleanRoomProvider;

public class FlexNodeProfile
{
    public bool Enabled { get; set; }

    public List<FlexNode>? Nodes { get; set; }
}

public class FlexNode
{
    public JsonObject K8sNodeDetails { get; set; } = default!;
}