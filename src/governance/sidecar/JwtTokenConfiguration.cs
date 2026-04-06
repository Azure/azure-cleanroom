// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class JwtTokenConfiguration(
    string tokenCredentialScope,
    CcfTokenCredential tokenCredential)
{
    public string TokenCredentialScope { get; set; } = tokenCredentialScope;

    public CcfTokenCredential TokenCredential { get; set; } = tokenCredential;
}
