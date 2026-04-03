// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Governance.Models;

#pragma warning disable SA1300 // Element should begin with upper-case letter
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AccountType
{
    /// <summary>
    /// Represents a Microsoft Account.
    /// </summary>
    microsoft
}
#pragma warning restore SA1300 // Element should begin with upper-case letter
