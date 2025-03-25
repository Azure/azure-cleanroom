// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CoseUtils;

namespace CcfProvider;

public class SigningConfiguration
{
    public CoseSignKey CoseSignKey { get; set; } = default!;
}