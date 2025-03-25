// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CoseUtils;

namespace Controllers;

public class SigningConfiguration
{
    public CoseSignKey SignKey { get; set; } = default!;

    public string MemberId { get; set; } = default!;
}