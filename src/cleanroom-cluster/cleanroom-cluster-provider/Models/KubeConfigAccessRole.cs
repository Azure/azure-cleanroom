// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CleanRoomProvider;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KubeConfigAccessRole
{
    /// <summary>
    /// Full admin access to the cluster.
    /// </summary>
    [JsonStringEnumMemberName("admin")]
    Admin,

    /// <summary>
    /// Read-only access to the cluster.
    /// </summary>
    [JsonStringEnumMemberName("readonly")]
    Readonly,

    /// <summary>
    /// Diagnostic access to the cluster (e.g. telemetry namespace).
    /// </summary>
    [JsonStringEnumMemberName("diagnostic")]
    Diagnostic
}