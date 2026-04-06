// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

/// <summary>
/// Defines the security mode for privacy proxy operations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProxyMode
{
    /// <summary>
    /// Secure mode with enhanced privacy and security controls.
    /// All data access and operations are performed with strict security policies,
    /// encryption, and access controls to ensure data privacy and integrity.
    /// </summary>
    [JsonStringEnumMemberName("Secure")]
    Secure,

    /// <summary>
    /// Open mode with relaxed security controls.
    /// Data access and operations are performed with standard security measures
    /// but without the enhanced privacy controls of secure mode.
    /// </summary>
    [JsonStringEnumMemberName("Open")]
    Open
}
