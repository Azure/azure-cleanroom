// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models.CCF;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EncryptionMode
{
    /// <summary>
    /// Client-side encryption.
    /// </summary>
    [JsonStringEnumMemberName("CSE")]
    CSE,

    /// <summary>
    /// Customer-provided key encryption.
    /// </summary>
    [JsonStringEnumMemberName("CPK")]
    CPK,

    /// <summary>
    /// Server-side encryption.
    /// </summary>
    [JsonStringEnumMemberName("SSE")]
    SSE
}