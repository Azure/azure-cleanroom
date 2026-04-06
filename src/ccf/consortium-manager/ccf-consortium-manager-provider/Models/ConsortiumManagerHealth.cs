// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfConsortiumMgrProvider;

public enum ServiceStatus
{
    /// <summary>
    /// Nothing from the service provider side is considered as an issue.
    /// </summary>
    Ok,

    /// <summary>
    /// Service may no longer function and might need to be replaced/restared.
    /// </summary>
    Unhealthy
}

public class ConsortiumManagerHealth
{
    public required string Name { get; set; }

    public required string Endpoint { get; set; }

    public required ServiceStatus Status { get; set; }

    public required List<Reason> Reasons { get; set; }
}

public class Reason
{
    public required string Code { get; set; }

    public required string Message { get; set; }
}
