﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class OperatorRecoveryInput
{
    // PEM encoded private key string.
    public string EncryptionPrivateKey { get; set; } = default!;
}