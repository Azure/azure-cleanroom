// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace CcfConsortiumMgr.Clients.Governance.Models;

public class EncryptedShareData
{
    [JsonPropertyName("encryptedShare")]
    public string EncryptedShare { get; set; } = default!;
}
