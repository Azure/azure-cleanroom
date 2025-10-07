// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Controllers;

internal class MsalCachedCcfTokenCredential : CcfTokenCredential
{
    // TODO (gsinha): Need to register a new app in AAD and use that for authentication
    // for the device code flow. The current client ID is used only for testing purposes.
    private static readonly string ClientId = "8a3849c1-81c5-4d62-b83e-3bb2bb11251a";
    private readonly string tokenCacheDir;

    public MsalCachedCcfTokenCredential(string? tokenCacheDir)
    {
        this.tokenCacheDir = tokenCacheDir ?? "/app/token_cache";
    }

    public override async ValueTask<string> GetTokenAsync(
        TokenRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var app = await this.BuildApp();
        try
        {
            var accounts = await app.GetAccountsAsync();
            var account = accounts.FirstOrDefault(
                a => a.Environment == "login.microsoftonline.com");
            if (account == null)
            {
                throw new Exception($"Unauthorized: No account found in cache.");
            }

            var result = await app.AcquireTokenSilent(requestContext.Scopes, account).ExecuteAsync();
            return result.IdToken;
        }
        catch (MsalUiRequiredException)
        {
            throw new Exception(
                "Unauthorized: code:reauth_required, message: User interaction required.");
        }
    }

    private async Task<IPublicClientApplication> BuildApp()
    {
        var app = PublicClientApplicationBuilder
            .Create(ClientId)
            .WithAuthority("https://login.microsoftonline.com/common")
            .WithDefaultRedirectUri()
            .Build();

        // The token cache is supposed to be pre-populated with the account that
        // AcquireTokenSilent is going to look for.
        var storageProps = new StorageCreationPropertiesBuilder(
            "token_cache.json",
            this.tokenCacheDir)
            .WithUnprotectedFile()
            .Build();

        var cacheHelper = await MsalCacheHelper.CreateAsync(storageProps);
        cacheHelper.RegisterCache(app.UserTokenCache);

        return app;
    }
}