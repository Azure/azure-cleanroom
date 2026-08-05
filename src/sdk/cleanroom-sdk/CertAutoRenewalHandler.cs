// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// <see cref="DelegatingHandler"/> that catches TLS
/// <see cref="AuthenticationException"/> and retries once after
/// downloading a fresh service certificate. Uses one-retry semantics
/// to prevent infinite loops on persistent certificate failures.
/// </summary>
internal sealed class CertAutoRenewalHandler : DelegatingHandler
{
    private const string RetryKey = "CertRefreshRetry";

    private readonly Func<Task<string>> certDownloader;
    private readonly CcfTlsHandler tlsHandler;
    private readonly ILogger? logger;

    public CertAutoRenewalHandler(
        Func<Task<string>> certDownloader,
        CcfTlsHandler tlsHandler,
        ILogger? logger)
        : base(tlsHandler.Handler)
    {
        this.certDownloader = certDownloader;
        this.tlsHandler = tlsHandler;
        this.logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(
            new HttpRequestOptionsKey<bool>(RetryKey),
            out var hasRetried))
        {
            hasRetried = false;
        }

        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        when (!hasRetried &&
              ex.InnerException is AuthenticationException)
        {
            this.logger?.LogWarning(
                "TLS validation failed for {Method} {Uri} " +
                "— downloading fresh certificate.",
                request.Method,
                request.RequestUri);

            string newCertPem = await this.certDownloader();
            this.tlsHandler.UpdateServiceCert(newCertPem);

            // Prevent retry loops.
            request.Options.Set(
                new HttpRequestOptionsKey<bool>(RetryKey), true);

            return await base.SendAsync(
                request, cancellationToken);
        }
    }
}
