// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CleanRoomProvider;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PodStatus
{
    /// <summary>
    /// Pod is healthy.
    /// </summary>
    Ok,

    /// <summary>
    /// Pod is not healthy.
    /// </summary>
    Error
}

public class CleanRoomClusterHealth
{
    public CleanRoomClusterHealth()
    {
        this.PodHealth = [];
    }

    public List<PodHealth> PodHealth { get; set; }
}

public class PodHealth
{
    public PodHealth(string ns, string name)
    {
        this.Namespace = ns;
        this.Name = name;
        this.Status = PodStatus.Ok;
        this.Reasons = [];
    }

    public string Namespace { get; set; }

    public string Name { get; set; }

    public PodStatus Status { get; set; }

    public List<Reason> Reasons { get; set; }
}

public class Reason
{
    public required string Code { get; set; }

    public required string Message { get; set; }
}
