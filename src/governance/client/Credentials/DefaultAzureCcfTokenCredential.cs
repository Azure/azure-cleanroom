// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Azure.Identity;

namespace Controllers;

internal class DefaultAzureCcfTokenCredential : CcfTokenCredential
{
    private DefaultAzureCredential creds;

    public DefaultAzureCcfTokenCredential()
    {
        this.creds = new DefaultAzureCredential();
    }

    public override async ValueTask<string> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        AccessToken accessToken = await this.creds.GetTokenAsync(
            requestContext,
            CancellationToken.None);
        return accessToken.Token;
    }
}