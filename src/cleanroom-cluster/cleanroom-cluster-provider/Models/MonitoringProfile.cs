// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public class MonitoringProfile
{
    public bool Enabled { get; set; } = default!;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KaitoProfile? KaitoProfile { get; set; } = default!;
}
