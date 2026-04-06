// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkloadType
{
    /// <summary>
    /// Analytics workload.
    /// </summary>
    Analytics,
}
