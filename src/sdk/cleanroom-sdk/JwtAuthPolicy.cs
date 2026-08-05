// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ClientModel.Primitives;
using Azure.Core;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// Pipeline policy that adds a JWT Bearer token to outgoing requests.
/// Injected into the generated
/// <see cref="Azure.Cleanroom.Governance.Client.GovernanceClient"/>
/// via <c>GovernanceClientOptions.AddPolicy()</c>.
/// Obtains and caches tokens from the configured <see cref="TokenCredential"/>.
/// </summary>
public class JwtAuthPolicy : PipelinePolicy
{
    private readonly TokenCredential tokenCredential;
    private readonly string[] scopes;
    private AccessToken? cachedToken;

    /// <summary>
    /// Initializes a new instance of the <see cref="JwtAuthPolicy"/> class.
    /// </summary>
    /// <param name="tokenCredential">The credential provider.</param>
    /// <param name="scope">The scope for token requests.</param>
    public JwtAuthPolicy(TokenCredential tokenCredential, string scope)
    {
        this.tokenCredential = tokenCredential ??
            throw new ArgumentNullException(nameof(tokenCredential));
        this.scopes = new[] { scope };
    }

    /// <inheritdoc/>
    public override void Process(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        this.AddAuthorizationHeader(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    /// <inheritdoc/>
    public override async ValueTask ProcessAsync(
        PipelineMessage message,
        IReadOnlyList<PipelinePolicy> pipeline,
        int currentIndex)
    {
        await this.AddAuthorizationHeaderAsync(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void AddAuthorizationHeader(PipelineMessage message)
    {
        if (this.cachedToken == null ||
            this.cachedToken.Value.ExpiresOn <=
                DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var context = new TokenRequestContext(this.scopes);
            this.cachedToken = this.tokenCredential.GetToken(
                context,
                default);
        }

        message.Request.Headers.Set(
            "Authorization",
            $"Bearer {this.cachedToken.Value.Token}");
    }

    private async ValueTask AddAuthorizationHeaderAsync(
        PipelineMessage message)
    {
        if (this.cachedToken == null ||
            this.cachedToken.Value.ExpiresOn <=
                DateTimeOffset.UtcNow.AddMinutes(2))
        {
            var context = new TokenRequestContext(this.scopes);
            this.cachedToken = await this.tokenCredential.GetTokenAsync(
                context,
                message.CancellationToken);
        }

        message.Request.Headers.Set(
            "Authorization",
            $"Bearer {this.cachedToken.Value.Token}");
    }
}
