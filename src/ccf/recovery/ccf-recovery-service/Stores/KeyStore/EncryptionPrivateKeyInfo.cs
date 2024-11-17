﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class EncryptionPrivateKeyInfo : EncryptionKeyInfo
{
    public string EncryptionPrivateKey { get; set; } = default!;
}