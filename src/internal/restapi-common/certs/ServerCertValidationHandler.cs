// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Controllers;

public class ServerCertValidationHandler : DelegatingHandler
{
    private List<string>? serviceCertPems;
    private ILogger logger;
    private X509Certificate2Collection? roots;

    public ServerCertValidationHandler(
        ILogger logger,
        string? serviceCertPem,
        bool skipTlsVerify = false,
        X509Certificate2? clientCert = null,
        string? endpointName = null)
        : this(
            logger,
            serviceCertPem == null ? [] : [serviceCertPem],
            skipTlsVerify,
            clientCert,
            endpointName)
    {
    }

    public ServerCertValidationHandler(
        ILogger logger,
        List<string> serviceCertPems,
        bool skipTlsVerify = false,
        X509Certificate2? clientCert = null,
        string? endpointName = null)
    {
        this.logger = logger;
        endpointName ??= "endpoint";
        this.SetRootCertsCollection(logger, serviceCertPems);
        var socketsHandler = new SocketsHttpHandler
        {
            // Avoid DNS refresh issues for long lived clients.
            // https://www.stevejgordon.co.uk/httpclient-connection-pooling-in-dotnet-core
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            SslOptions =
            {
                ClientCertificates = clientCert != null ?
                    new X509Certificate2Collection(clientCert) : null,
                RemoteCertificateValidationCallback = (request, cert1, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                    {
                        return true;
                    }

                    if (cert1 == null || chain == null)
                    {
                        return false;
                    }

                    if (this.roots == null)
                    {
                        if (skipTlsVerify)
                        {
                            return true;
                        }

                        logger.LogError(
                            "Failing SSL cert validation callback as no SSL cert to use for " +
                            $"verification was specified.");
                        return false;
                    }

                    X509Certificate2 cert = (X509Certificate2)cert1;
                    foreach (X509ChainElement element in chain.ChainElements)
                    {
                        chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                    }

                    chain.ChainPolicy.CustomTrustStore.Clear();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.AddRange(this.roots);
                    var result = chain.Build(cert);
                    if (!result)
                    {
                        logger.LogError(
                            $"{endpointName}: Failing SSL cert validation callback for " +
                            $"as chain.Build() returned false.");
                        for (int index = 0; index < chain.ChainStatus.Length; index++)
                        {
                            logger.LogError($"{endpointName}: chainStatus[{index}]: " +
                                $"{chain.ChainStatus[0].Status}, " +
                                $"{chain.ChainStatus[0].StatusInformation}");
                        }

                        logger.LogError(
                            $"{endpointName}: Incoming cert PEM: {cert.ExportCertificatePem()}");
                        logger.LogError(
                            $"Expected cert PEMs are: " +
                            $"{JsonSerializer.Serialize(this.serviceCertPems)}");
                    }

                    return result;
                }
            }
        };
        this.InnerHandler = socketsHandler;
    }

    public void UpdateServiceCert(string serviceCertPem)
    {
        this.SetRootCertsCollection(this.logger, [serviceCertPem]);
    }

    private void SetRootCertsCollection(ILogger logger, List<string> serviceCertPems)
    {
        X509Certificate2Collection? roots = null;
        if (serviceCertPems.Any())
        {
            try
            {
                roots = new X509Certificate2Collection();
                foreach (var certPem in serviceCertPems)
                {
                    roots.Add(X509Certificate2.CreateFromPem(certPem));
                }
            }
            catch (Exception e)
            {
                this.logger.LogError(e, "Unexpected failure in loading cert PEM.");
                throw;
            }
        }

        this.serviceCertPems = serviceCertPems;
        this.roots = roots;
    }
}
