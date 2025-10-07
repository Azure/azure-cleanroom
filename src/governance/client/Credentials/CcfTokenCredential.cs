// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;

namespace Controllers;

/// <summary>
/// Represents a credential capable of providing a JWT token used for identification.
/// </summary>
public abstract class CcfTokenCredential
{
    public abstract ValueTask<string> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken);
}
