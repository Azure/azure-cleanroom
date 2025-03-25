// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfRecoveryProvider;

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

public class RecoveryServiceHealth
{
    public string Name { get; set; } = default!;

    public string Endpoint { get; set; } = default!;

    // ServiceStatus string value.
    public string Status { get; set; } = default!;

    public List<Reason> Reasons { get; set; } = default!;
}

public class Reason
{
    public string Code { get; set; } = default!;

    public string Message { get; set; } = default!;
}
