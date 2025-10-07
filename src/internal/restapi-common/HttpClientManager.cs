// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
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
        IAsyncPolicy<HttpResponseMessage> retryPolicy)
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

        var policyHandler = new PolicyHttpMessageHandler(retryPolicy)
        {
            InnerHandler = sslVerifyHandler
        };
        return new HttpClient(policyHandler)
        {
            BaseAddress = new Uri(endpoint)
        };
    }

    public HttpClient GetOrAddClient(
        string endpoint,
        IAsyncPolicy<HttpResponseMessage> retryPolicy,
        string? endpointCert = null,
        string? endpointName = null,
        bool skipTlsVerify = false)
    {
        var endpointCerts = new List<string>();
        if (!string.IsNullOrEmpty(endpointCert))
        {
            endpointCerts.Add(endpointCert);
        }

        return this.GetOrAddClient(
            endpoint,
            endpointCerts,
            retryPolicy,
            endpointName,
            skipTlsVerify);
    }

    public HttpClient GetOrAddClient(
        string endpoint,
        List<string> endpointCerts,
        IAsyncPolicy<HttpResponseMessage> retryPolicy,
        string? endpointName = null,
        bool skipTlsVerify = false)
    {
        string key = ToKey(endpoint, endpointCerts, retryPolicy.PolicyKey);

        if (this.clients.TryGetValue(key, out var client))
        {
            return client;
        }

        client = this.InitializeClient(
            endpoint,
            endpointCerts,
            endpointName,
            skipTlsVerify,
            retryPolicy);
        if (!this.clients.TryAdd(key, client))
        {
            client.Dispose();
        }

        return this.clients[key];
    }

    public async Task<HttpClient> GetOrAddClient(
        string endpoint,
        ServiceCertLocator endpointCertLocator,
        IAsyncPolicy<HttpResponseMessage> retryPolicy,
        string? endpointName = null)
    {
        string key = ToKey(endpoint, endpointCertLocator, retryPolicy.PolicyKey);

        if (this.clients.TryGetValue(key, out var client))
        {
            return client;
        }

        // Before initializing the client invoke the cert locator to check that an acceptable
        // cert is available to start with. Later on if the cert changes then the cert locator
        // will get invoked again.
        var initialServiceCertPem = await endpointCertLocator.DownloadServiceCertificatePem();

        client = this.InitializeClient(
            endpoint,
            initialServiceCertPem,
            endpointCertLocator,
            endpointName,
            retryPolicy);
        if (!this.clients.TryAdd(key, client))
        {
            client.Dispose();
        }

        return this.clients[key];
    }

    private static string ToKey(string endpoint, List<string> endpointCerts, string retryPolicyKey)
    {
        if (!endpointCerts.Any())
        {
            return endpoint + "_" + retryPolicyKey;
        }

        return endpoint + "_" + string.Join("_", endpointCerts) + "_" + retryPolicyKey;
    }

    private static string ToKey(
        string endpoint,
        ServiceCertLocator certLocator,
        string retryPolicyKey)
    {
        return endpoint + "_" + certLocator.CertificateDiscoveryEndpoint + "_" + retryPolicyKey;
    }

    private HttpClient InitializeClient(
        string endpoint,
        List<string> endpointCerts,
        string? endpointName,
        bool skipTlsVerify,
        IAsyncPolicy<HttpResponseMessage> retryPolicy)
    {
        var sslVerifyHandler =
            new ServerCertValidationHandler(
                this.logger,
                endpointCerts,
                skipTlsVerify,
                endpointName: endpointName);

        if (!endpoint.StartsWith("http"))
        {
            endpoint = "https://" + endpoint;
        }

        var policyHandler = new PolicyHttpMessageHandler(retryPolicy)
        {
            InnerHandler = sslVerifyHandler
        };
        var client = new HttpClient(policyHandler)
        {
            BaseAddress = new Uri(endpoint)
        };
        return client;
    }

    private HttpClient InitializeClient(
        string endpoint,
        string initialServiceCertPem,
        ServiceCertLocator certLocator,
        string? endpointName,
        IAsyncPolicy<HttpResponseMessage> retryPolicy)
    {
        var sslVerifyHandler = new ServerCertValidationHandler(
            this.logger,
            serviceCertPem: initialServiceCertPem,
            skipTlsVerify: false,
            endpointName: endpointName);
        var autoRenewingCertHandler = new AutoRenewingCertHandler(
            this.logger,
            certLocator,
            sslVerifyHandler,
            onRenewal: (serviceCertPem) => { });

        if (!endpoint.StartsWith("http"))
        {
            endpoint = "https://" + endpoint;
        }

        // The chain is:
        // retryPolicyHandler ->
        //   AutoRenewingCertHandler ->
        //     ServerCertValidationHandler
        var policyHandler = new PolicyHttpMessageHandler(retryPolicy)
        {
            InnerHandler = autoRenewingCertHandler
        };
        var client = new HttpClient(policyHandler)
        {
            BaseAddress = new Uri(endpoint)
        };

        return client;
    }
}
