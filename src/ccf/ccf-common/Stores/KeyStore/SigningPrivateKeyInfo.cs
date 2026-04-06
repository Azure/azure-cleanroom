// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CcfCommon;

public class SigningPrivateKeyInfo : SigningKeyInfo
{
    public string SigningKey { get; set; } = default!;
}