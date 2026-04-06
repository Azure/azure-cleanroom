// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccessMode
{
    /// <summary>
    /// Read.
    /// </summary>
    [JsonStringEnumMemberName("read")]
    Read,

    /// <summary>
    /// Write.
    /// </summary>
    [JsonStringEnumMemberName("write")]
    Write,
}
