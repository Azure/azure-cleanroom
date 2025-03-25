// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Polly;

namespace Controllers;

public class HttpClientManager
{
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, HttpClient> clients =
        new(StringComparer.OrdinalIgnoreCase);

    public HttpClientManager(ILogger logger)
    {
        this.logger = logger;
    }

    public static HttpClient NewInsecureClient(
        string endpoint,
        ILogger logger,
        IAsyncPolicy<HttpResponseMessage>? retryPolicy = null)
    {
        if (!endpoint.StartsWith("http"))
        {
            endpoint = "https://" + endpoint;
        }

        var sslVerifyHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                return true;
            }
        };

        HttpMessageHandler handler = sslVerifyHandler;
        if (retryPolicy != null)
        {
            var policyHandler = new PolicyHttpMessageHandler(retryPolicy);
            policyHandler.InnerHandler = handler;
            handler = policyHandler;
        }

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(endpoint)
        };
    }

    public HttpClient GetOrAddClient(
        string endpoint,
        string? endpointCert = null,
        string? endpointName = null,
        bool skipTlsVerify = false,
        IAsyncPolicy<HttpResponseMessage>? retryPolicy = null)
    {
        var endpointCerts = new List<string>();
        if (!string.IsNullOrEmpty(endpointCert))
        {
            endpointCerts.Add(endpointCert);
        }

        return this.GetOrAddClient(
            endpoint,
            endpointCerts,
            endpointName,
            skipTlsVerify,
            retryPolicy);
    }

    public HttpClient GetOrAddClient(
        string endpoint,
        List<string> endpointCerts,
        string? endpointName = null,
        bool skipTlsVerify = false,
        IAsyncPolicy<HttpResponseMessage>? retryPolicy = null)
    {
        string key = ToKey(endpoint, endpointCerts);

        if (this.clients.TryGetValue(key, out var client))
        {
            return client;
        }

        client =
            this.InitializeClient(endpoint, endpointCerts, endpointName, skipTlsVerify, retryPolicy);
        if (!this.clients.TryAdd(key, client))
        {
            client.Dispose();
        }

        return this.clients[key];
    }

    private static string ToKey(string endpoint, List<string> endpointCerts)
    {
        if (!endpointCerts.Any())
        {
            return endpoint;
        }

        return endpoint + "_" + string.Join("_", endpointCerts);
    }

    private HttpClient InitializeClient(
        string endpoint,
        List<string> endpointCerts,
        string? endpointName,
        bool skipTlsVerify,
        IAsyncPolicy<HttpResponseMessage>? retryPolicy)
    {
        X509Certificate2Collection? roots = null;
        if (endpointCerts.Any())
        {
            try
            {
                roots = new X509Certificate2Collection();
                foreach (var certPem in endpointCerts)
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

        var sslVerifyHandler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None)
                {
                    return true;
                }

                if (cert == null || chain == null)
                {
                    return false;
                }

                if (roots == null)
                {
                    if (skipTlsVerify)
                    {
                        return true;
                    }

                    this.logger.LogError(
                        "Failing SSL cert validation callback as no SSL cert to use for " +
                        $"verification of endpoint '{endpoint}' was specified.");
                    return false;
                }

                foreach (X509ChainElement element in chain.ChainElements)
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }

                chain.ChainPolicy.CustomTrustStore.Clear();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                var result = chain.Build(cert);
                if (!result)
                {
                    this.logger.LogError(
                        $"{endpointName}: Failing SSL cert validation callback for " +
                        $"{endpoint} as " +
                        $"chain.Build() returned false.");
                    for (int index = 0; index < chain.ChainStatus.Length; index++)
                    {
                        this.logger.LogError($"{endpointName}: chainStatus[{index}]: " +
                            $"{chain.ChainStatus[0].Status}, " +
                            $"{chain.ChainStatus[0].StatusInformation}");
                    }

                    this.logger.LogError($"{endpointName}: Incoming cert PEM: " +
                        $"{cert.ExportCertificatePem()}");
                    this.logger.LogError($"{endpointName}: Expected cert PEMs are: " +
                        $"{JsonConvert.SerializeObject(endpointCerts)}");
                }

                return result;
            }
        };

        if (!endpoint.StartsWith("http"))
        {
            endpoint = "https://" + endpoint;
        }

        HttpMessageHandler handler = sslVerifyHandler;
        if (retryPolicy != null)
        {
            var policyHandler = new PolicyHttpMessageHandler(retryPolicy);
            policyHandler.InnerHandler = handler;
            handler = policyHandler;
        }

        var client = new HttpClient(handler);
        client.BaseAddress = new Uri(endpoint);
        return client;
    }
}
