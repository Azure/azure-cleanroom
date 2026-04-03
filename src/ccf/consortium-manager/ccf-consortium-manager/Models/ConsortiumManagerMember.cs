// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfConsortiumMgr.Models;

public class ConsortiumManagerMember
{
    public required string SigningKey { get; set; }

    public required string EncryptionKey { get; set; }
}
