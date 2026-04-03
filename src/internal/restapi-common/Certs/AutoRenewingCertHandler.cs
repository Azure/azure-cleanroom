// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace Controllers;

public class AutoRenewingCertHandler : DelegatingHandler
{
    private readonly ILogger logger;
    private readonly ServiceCertLocator certLocator;
    private readonly Action<string> onRenewal;

    public AutoRenewingCertHandler(
        ILogger logger,
        ServiceCertLocator certLocator,
        ServerCertValidationHandler serverCertValidationHandler,
        Action<string> onRenewal)
        : base(serverCertValidationHandler)
    {
        this.logger = logger;
        this.certLocator = certLocator;
        this.onRenewal = onRenewal;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        const string RetryKey = "CertRefreshRetry";

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
        when (!hasRetried && ex.InnerException is AuthenticationException)
        {
            this.logger.LogWarning($"SSL validation failed during " +
                $"{request.Method} {request.RequestUri} — redownloading certificate.");

            string serviceCertPem = await this.certLocator.DownloadServiceCertificatePem();

            this.onRenewal(serviceCertPem);

            // Update the SSL cert being used by the InnerHandler for validation.
            if (this.InnerHandler is not ServerCertValidationHandler certValidationHandler)
            {
                throw new Exception(
                    $"InnerHandler was expected of type ServerCertValidationHandler but is " +
                    $"{this.InnerHandler?.GetType().ToString()}");
            }

            certValidationHandler.UpdateServiceCert(serviceCertPem);

            // Set the retry flag so we don't retry again if the same failure happens again.
            request.Options.Set(new HttpRequestOptionsKey<bool>(RetryKey), true);

            return await base.SendAsync(request, cancellationToken); // Retry once.
        }
    }
}
