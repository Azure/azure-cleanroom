// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.Extensions.Http;

namespace Controllers;

public class CcfClientManager
{
    private readonly ILogger logger;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private WorkspaceConfiguration wsConfig = default!;
    private HttpClient ccfAppClient = default!;
    private IConfiguration config;

    public CcfClientManager(
        ILogger logger,
        IConfiguration config)
    {
        this.logger = logger;
        this.config = config;
    }

    public async Task<HttpClient> GetAppClient()
    {
        await this.InitializeAppClient();
        return this.ccfAppClient;
    }

    public async Task<WorkspaceConfiguration> GetWsConfig()
    {
        await this.InitializeWsConfig();
        return this.wsConfig;
    }

    private async Task InitializeAppClient()
    {
        await this.InitializeWsConfig();
        if (this.ccfAppClient == null)
        {
            try
            {
                await this.semaphore.WaitAsync();
                if (this.ccfAppClient == null)
                {
                    this.ccfAppClient = this.InitializeClient();
                }
            }
            finally
            {
                this.semaphore.Release();
            }
        }
    }

    private HttpClient InitializeClient()
    {
        var ccrgovEndpoint = new Uri(this.wsConfig.CcrgovEndpoint);

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

                if (string.IsNullOrEmpty(this.wsConfig.ServiceCert))
                {
                    this.logger.LogError(
                        "Failing SSL cert validation callback as no ServiceCert specified.");
                    return false;
                }

                foreach (X509ChainElement element in chain.ChainElements)
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }

                var roots = new X509Certificate2Collection(
                    X509Certificate2.CreateFromPem(this.wsConfig.ServiceCert));

                chain.ChainPolicy.CustomTrustStore.Clear();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                return chain.Build(cert);
            }
        };

        var policyHandler = new PolicyHttpMessageHandler(
            HttpRetries.Policies.GetDefaultRetryPolicy(this.logger));
        policyHandler.InnerHandler = handler;
        var client = new HttpClient(policyHandler);
        client.BaseAddress = ccrgovEndpoint;
        return client;
    }

    private async Task InitializeWsConfig()
    {
        if (this.wsConfig == null)
        {
            try
            {
                await this.semaphore.WaitAsync();
                if (this.wsConfig == null)
                {
                    if (string.IsNullOrEmpty(this.config[SettingName.AttestationReport]))
                    {
                        this.wsConfig = await this.InitializeWsConfigFetchAttestation();
                    }
                    else
                    {
                        this.wsConfig = await this.InitializeWsConfigFromEnvironment();
                    }
                }
            }
            finally
            {
                this.semaphore.Release();
            }
        }
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigFromEnvironment()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcrGovEndpoint]))
        {
            throw new ArgumentException("ccrgovEndpoint environment variable must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.CcrgovPrivKey]))
        {
            throw new ArgumentException($"{SettingName.CcrgovPrivKey} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.CcrgovPubKey]))
        {
            throw new ArgumentException($"{SettingName.CcrgovPubKey} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.AttestationReport]))
        {
            throw new ArgumentException(
                $"{SettingName.AttestationReport} setting must be specified.");
        }

        string? serviceCert = await this.GetServiceCertAsync();

        var wsConfig = new WorkspaceConfiguration
        {
            CcrgovEndpoint = this.config[SettingName.CcrGovEndpoint]!,
            ServiceCert = serviceCert
        };

        var privateKey =
            await File.ReadAllTextAsync(this.config[SettingName.CcrgovPrivKey]!);
        var publicKey =
            await File.ReadAllTextAsync(this.config[SettingName.CcrgovPubKey]!);
        var content = await File.ReadAllTextAsync(this.config[SettingName.AttestationReport]!);
        var attestationReport = JsonSerializer.Deserialize<AttestationReport>(content)!;

        wsConfig.Attestation = new AttestationReportKey(publicKey, privateKey, attestationReport);
        return wsConfig;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigFetchAttestation()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcrGovEndpoint]))
        {
            throw new ArgumentException("ccrgovEndpoint environment variable must be specified.");
        }

        string? serviceCert = await this.GetServiceCertAsync();

        var wsConfig = new WorkspaceConfiguration
        {
            CcrgovEndpoint = this.config[SettingName.CcrGovEndpoint]!,
            ServiceCert = serviceCert
        };

        wsConfig.Attestation = await Attestation.GenerateRsaKeyPairAndReportAsync();
        return wsConfig;
    }

    private async Task<string?> GetServiceCertAsync()
    {
        if (!string.IsNullOrEmpty(this.config[SettingName.ServiceCert]))
        {
            byte[] serviceCert = Convert.FromBase64String(this.config[SettingName.ServiceCert]!);
            return Encoding.UTF8.GetString(serviceCert);
        }

        if (!string.IsNullOrEmpty(this.config[SettingName.ServiceCertPath]))
        {
            return await File.ReadAllTextAsync(this.config[SettingName.ServiceCertPath]!);
        }

        return null;
    }
}
