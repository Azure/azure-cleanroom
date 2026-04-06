// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecretType
{
    /// <summary>
    /// Secret.
    /// </summary>
    Secret,

    /// <summary>
    /// Certificate.
    /// </summary>
    Certificate,

    /// <summary>
    /// Key.
    /// </summary>
    Key,
}
