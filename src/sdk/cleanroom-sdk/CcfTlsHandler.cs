// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// Creates a <see cref="SocketsHttpHandler"/> configured for CCF's
/// self-signed TLS certificates. Validates the server cert against
/// the provided PEM chain and optionally attaches a client cert
/// for mTLS. Exposes <see cref="UpdateServiceCert"/> for runtime
/// cert rotation (used by auto-renewal).
/// </summary>
internal sealed class CcfTlsHandler
{
    private readonly ILogger? logger;
    private X509Certificate2Collection? roots;
    private List<string> serviceCertPems;

    /// <summary>
    /// Initializes a new instance of the <see cref="CcfTlsHandler"/>
    /// class from a single PEM string.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serviceCertPem">
    /// The service certificate PEM string.
    /// </param>
    /// <param name="skipTlsVerify">
    /// Whether to skip TLS verification.
    /// </param>
    /// <param name="clientCert">Optional client certificate.</param>
    public CcfTlsHandler(
        ILogger? logger,
        string? serviceCertPem,
        bool skipTlsVerify = false,
        X509Certificate2? clientCert = null)
        : this(
            logger,
            serviceCertPem == null ? [] : [serviceCertPem],
            skipTlsVerify,
            clientCert)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CcfTlsHandler"/>
    /// class from a list of PEM strings.
    /// </summary>
    /// <param name="logger">Optional logger.</param>
    /// <param name="serviceCertPems">
    /// The service certificate PEM strings.
    /// </param>
    /// <param name="skipTlsVerify">
    /// Whether to skip TLS verification.
    /// </param>
    /// <param name="clientCert">Optional client certificate.</param>
    public CcfTlsHandler(
        ILogger? logger,
        List<string> serviceCertPems,
        bool skipTlsVerify = false,
        X509Certificate2? clientCert = null)
    {
        this.logger = logger;
        this.serviceCertPems = serviceCertPems;
        this.SetRootCertsCollection(serviceCertPems);

        this.Handler = new SocketsHttpHandler
        {
            // Avoid DNS refresh issues for long-lived clients.
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            SslOptions =
            {
                ClientCertificates = clientCert != null
                    ? new X509Certificate2Collection(clientCert)
                    : null,
                RemoteCertificateValidationCallback =
                    this.CreateValidationCallback(skipTlsVerify)
            }
        };
    }

    /// <summary>
    /// Gets the underlying <see cref="SocketsHttpHandler"/>. Share it
    /// across multiple <see cref="HttpClient"/> instances with
    /// <c>disposeHandler: false</c>.
    /// </summary>
    public SocketsHttpHandler Handler { get; }

    /// <summary>
    /// Replaces the trusted service certificate at runtime.
    /// Called by the auto-renewal handler on TLS failure.
    /// </summary>
    /// <param name="serviceCertPem">
    /// The new service certificate PEM string.
    /// </param>
    public void UpdateServiceCert(string serviceCertPem)
    {
        this.SetRootCertsCollection([serviceCertPem]);
    }

    private RemoteCertificateValidationCallback
        CreateValidationCallback(bool skipTlsVerify)
    {
        return (request, cert, chain, errors) =>
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (cert == null || chain == null)
            {
                return false;
            }

            if (this.roots == null)
            {
                if (skipTlsVerify)
                {
                    return true;
                }

                this.logger?.LogError(
                    "TLS validation failed: no service " +
                    "certificate configured.");
                return false;
            }

            foreach (X509ChainElement element in chain.ChainElements)
            {
                chain.ChainPolicy.ExtraStore.Add(
                    element.Certificate);
            }

            chain.ChainPolicy.CustomTrustStore.Clear();
            chain.ChainPolicy.TrustMode =
                X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(this.roots);

            var result = chain.Build((X509Certificate2)cert);
            if (!result)
            {
                this.logger?.LogError(
                    "TLS validation failed: chain.Build() " +
                    "returned false.");
                for (int i = 0; i < chain.ChainStatus.Length; i++)
                {
                    this.logger?.LogError(
                        "chainStatus[{Index}]: {Status}, {Info}",
                        i,
                        chain.ChainStatus[i].Status,
                        chain.ChainStatus[i].StatusInformation);
                }

                this.logger?.LogError(
                    "Server cert: {Pem}",
                    ((X509Certificate2)cert).ExportCertificatePem());
                this.logger?.LogError(
                    "Expected certs: {Pems}",
                    JsonSerializer.Serialize(this.serviceCertPems));
            }

            return result;
        };
    }

    private void SetRootCertsCollection(List<string> serviceCertPems)
    {
        X509Certificate2Collection? roots = null;
        if (serviceCertPems.Count > 0)
        {
            roots = [];
            foreach (var certPem in serviceCertPems)
            {
                roots.Add(X509Certificate2.CreateFromPem(certPem));
            }
        }

        this.serviceCertPems = serviceCertPems;
        this.roots = roots;
    }
}