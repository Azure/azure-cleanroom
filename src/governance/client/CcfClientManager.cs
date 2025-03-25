// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CoseUtils;
using Microsoft.Extensions.Http;

namespace Controllers;

public class CcfClientManager
{
    private static CcfConfiguration ccfConfiguration = new();
    private static SigningConfiguration signingConfiguration = default!;
    private static X509Certificate2 httpsClientCert = default!;
    private static ConcurrentDictionary<string, string> secretIdToSecretMap = new();
    private readonly ILogger logger;
    private readonly string ccfEndpoint;
    private readonly string serviceCertPem;
    private string version = default!;

    public CcfClientManager(
        ILogger logger,
        string? ccfEndpoint,
        string? serviceCertPem)
    {
        this.logger = logger;
        this.ccfEndpoint = ccfEndpoint ?? ccfConfiguration.CcfEndpoint;
        this.serviceCertPem = serviceCertPem ?? ccfConfiguration.ServiceCert;
    }

    public static void SetGovAuthDefaults(
        CoseSignKey coseSignKey)
    {
        using var cert = X509Certificate2.CreateFromPem(coseSignKey.Certificate);
        signingConfiguration = new SigningConfiguration()
        {
            SignKey = coseSignKey,
            MemberId = cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower()
        };
    }

    public static void SetAppAuthDefaults(X509Certificate2 httpsClientCert)
    {
        CcfClientManager.httpsClientCert = httpsClientCert;
    }

    public static void SetCcfDefaults(string ccfEndpoint, string serviceCertPem)
    {
        ccfConfiguration = new CcfConfiguration()
        {
            CcfEndpoint = ccfEndpoint,
            ServiceCert = serviceCertPem
        };
    }

    public WorkspaceConfiguration GetWsConfig()
    {
        return new WorkspaceConfiguration()
        {
            CcfEndpoint = this.ccfEndpoint,
            ServiceCert = this.serviceCertPem,
            SigningCert = signingConfiguration.SignKey.Certificate,
            SigningKey = signingConfiguration.SignKey.PrivateKey,
            SigningCertId = signingConfiguration.SignKey.KvCertificate?.Id.ToString(),
            MemberId = signingConfiguration.MemberId,
        };
    }

    public string GetMemberId()
    {
        return signingConfiguration.MemberId;
    }

    public CoseSignKey GetCoseSignKey()
    {
        return signingConfiguration.SignKey;
    }

    public Task<HttpClient> GetGovClient()
    {
        var client = this.InitializeClient(configureClientCert: false);
        this.version = "2024-07-01";
        return Task.FromResult(client);
    }

    public HttpClient GetAppClient()
    {
        var client = this.InitializeClient(configureClientCert: true);
        return client;
    }

    public string GetGovApiVersion()
    {
        return this.version;
    }

    private HttpClient InitializeClient(bool configureClientCert)
    {
        if (signingConfiguration == null)
        {
            throw new Exception("Invoke /configure first to setup signing configuration.");
        }

        if (string.IsNullOrEmpty(this.ccfEndpoint) || string.IsNullOrEmpty(this.serviceCertPem))
        {
            throw new Exception("CCF endpoint and Service Certificate are mandatory");
        }

        var handler = new HttpClientHandler
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

                if (string.IsNullOrEmpty(this.serviceCertPem))
                {
                    this.logger.LogError(
                        "Failing SSL cert validation callback as no ServiceCert specified.");
                    return false;
                }

                foreach (X509ChainElement element in chain.ChainElements)
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }

                X509Certificate2Collection roots;
                try
                {
                    roots = new X509Certificate2Collection(
                        X509Certificate2.CreateFromPem(this.serviceCertPem));
                }
                catch (Exception e)
                {
                    this.logger.LogError(
                        e,
                        "Unexpected failure in loading service cert PEM.");
                    throw;
                }

                chain.ChainPolicy.CustomTrustStore.Clear();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                var result = chain.Build(cert);
                if (!result)
                {
                    this.logger.LogError(
                        "Failing SSL cert validation callback as chain.Build() " +
                        "returned false.");
                    for (int index = 0; index < chain.ChainStatus.Length; index++)
                    {
                        this.logger.LogError($"chainStatus[{index}]: " +
                            $"{chain.ChainStatus[0].Status}, " +
                            $"{chain.ChainStatus[0].StatusInformation}");
                    }

                    this.logger.LogError($"Incoming cert PEM: " +
                        $"{cert.ExportCertificatePem()}");
                    this.logger.LogError($"Expected cert PEMs are: " +
                        $"{this.serviceCertPem}");
                }

                return result;
            }
        };

        if (configureClientCert)
        {
            handler.ClientCertificates.Add(httpsClientCert);
        }

        var policyHandler = new PolicyHttpMessageHandler(
            HttpRetries.Policies.GetDefaultRetryPolicy(this.logger));
        policyHandler.InnerHandler = handler;
        var client = new HttpClient(policyHandler);
        client.BaseAddress = new Uri(this.ccfEndpoint);
        return client;
    }
}