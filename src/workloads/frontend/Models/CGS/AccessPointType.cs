// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessPointType
{
    /// <summary>
    /// Represents volume read write.
    /// </summary>
    [JsonStringEnumMemberName("Volume_ReadWrite")]
    Volume_ReadWrite,

    /// <summary>
    /// Represents volume read only.
    /// </summary>
    [JsonStringEnumMemberName("Volume_ReadOnly")]
    Volume_ReadOnly,
}