// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IdentityType
{
    /// <summary>
    /// Entra ID user accounts.
    /// </summary>
    User,

    /// <summary>
    /// Entra ID service principals.
    /// </summary>
    ServicePrincipal
}
