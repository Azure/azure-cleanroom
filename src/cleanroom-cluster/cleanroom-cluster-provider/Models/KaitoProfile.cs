// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class KaitoProfile
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KaitoWorkspace? Workspace { get; set; }

    public string? ModelEndpoint { get; set; }
}

public class KaitoWorkspace
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? Status { get; set; }
}
