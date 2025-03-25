// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AttestationClient;
using CcfCommon;

namespace Controllers;

public class ClientManager
{
    private ILogger logger;
    private IConfiguration config;
    private HttpClientManager httpClientManager;
    private WorkspaceConfiguration wsConfig = default!;
    private SemaphoreSlim semaphore = new(1, 1);

    public ClientManager(ILogger logger, IConfiguration config)
    {
        this.logger = logger;
        this.config = config;
        this.httpClientManager = new(logger);
    }

    public async Task<HttpClient> GetRecoverySvcClient(string? svcEndpoint, string? svcEndpointCert)
    {
        return await this.GetOrAddServiceClient(svcEndpoint, svcEndpointCert);
    }

    public async Task<HttpClient> GetCcfClient()
    {
        return await this.GetOrCreateCcfClient();
    }

    public async Task<WorkspaceConfiguration> GetWsConfig()
    {
        return await this.GetOrLoadWsConfig();
    }

    public string GetGovApiVersion()
    {
        return "2024-07-01";
    }

    private async Task<HttpClient> GetOrAddServiceClient(
        string? svcEndpoint,
        string? svcEndpointCert)
    {
        await this.GetOrLoadWsConfig();
        svcEndpoint ??= this.wsConfig.CcfRecoverySvcEndpoint;
        if (string.IsNullOrEmpty(svcEndpoint))
        {
            throw new ArgumentException(
                $"{SettingName.CcfRecoverySvcEndpoint} environment " +
                $"variable must be specified.");
        }

        svcEndpointCert = svcEndpointCert ?? this.wsConfig.CcfRecoverySvcEndpointCert;

        var client = this.httpClientManager.GetOrAddClient(
            svcEndpoint,
            svcEndpointCert,
            "recovery-service",
            skipTlsVerify: this.wsConfig.CcfRecoverySvcEndpointSkipTlsVerify);
        return client;
    }

    private async Task<HttpClient> GetOrCreateCcfClient()
    {
        await this.GetOrLoadWsConfig();
        var client = this.httpClientManager.GetOrAddClient(
            this.wsConfig.CcfEndpoint,
            this.wsConfig.CcfEndpointCert,
            "ccf-endpoint",
            this.wsConfig.CcfEndpointSkipTlsVerify);
        return client;
    }

    private async Task<WorkspaceConfiguration> GetOrLoadWsConfig()
    {
        if (this.wsConfig == null)
        {
            try
            {
                await this.semaphore.WaitAsync();
                if (this.wsConfig == null)
                {
                    if (CcfUtils.IsSevSnp())
                    {
                        this.wsConfig = await this.InitializeWsConfigFetchAttestation();
                    }
                    else
                    {
                        this.logger.LogWarning(
                            "Running in insecure-virtual mode. This is for dev/test environment.");
                        this.wsConfig = await this.InitializeWsConfigFromEnvironment();
                    }
                }
            }
            finally
            {
                this.semaphore.Release();
            }
        }

        return this.wsConfig;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigFromEnvironment()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcfEndpoint]))
        {
            throw new ArgumentException("CcfEndpoint environment variable must be specified.");
        }

        string? serviceCert = await this.GetCcfEndpointCertAsync();
        string? ccfRecoverySvcEndpointCert = await this.GetCcfRecoverySvcEndpointCertAsync();

        bool.TryParse(
            this.config[SettingName.CcfEndpointSkipTlsVerify],
            out var ccfEndpointSkipTlsVerify);

        bool.TryParse(
            this.config[SettingName.CcfRecoverySvcEndpointSkipTlsVerify],
            out var ccfRecoverySvcEndpointSkipTlsVerify);

        var wsConfig = new WorkspaceConfiguration
        {
            CcfEndpoint = this.config[SettingName.CcfEndpoint]!,
            CcfEndpointCert = serviceCert,
            CcfEndpointSkipTlsVerify = ccfEndpointSkipTlsVerify,
            CcfRecoverySvcEndpoint = this.config[SettingName.CcfRecoverySvcEndpoint],
            CcfRecoverySvcEndpointCert = ccfRecoverySvcEndpointCert,
            CcfRecoverySvcEndpointSkipTlsVerify = ccfRecoverySvcEndpointSkipTlsVerify
        };

        var privateKey =
            await File.ReadAllTextAsync("/app/insecure-virtual/keys/priv_key.pem");
        var publicKey =
            await File.ReadAllTextAsync("/app/insecure-virtual/keys/pub_key.pem");
        var content = await File.ReadAllTextAsync(
            "/app/insecure-virtual/attestation/attestation-report.json");
        var attestationReport = JsonSerializer.Deserialize<AttestationReport>(content)!;

        wsConfig.Attestation = new AttestationReportKey(publicKey, privateKey, attestationReport);
        return wsConfig;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigFetchAttestation()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcfEndpoint]))
        {
            throw new ArgumentException(
                $"{SettingName.CcfEndpoint} environment variable must be specified.");
        }

        string? ccfEndpointCert = await this.GetCcfEndpointCertAsync();
        string? ccfRecoverySvcEndpointCert = await this.GetCcfRecoverySvcEndpointCertAsync();

        bool.TryParse(
            this.config[SettingName.CcfEndpointSkipTlsVerify],
            out var ccfEndpointSkipTlsVerify);

        bool.TryParse(
            this.config[SettingName.CcfRecoverySvcEndpointSkipTlsVerify],
            out var ccfRecoverySvcEndpointSkipTlsVerify);

        var wsConfig = new WorkspaceConfiguration
        {
            CcfEndpoint = this.config[SettingName.CcfEndpoint]!,
            CcfEndpointCert = ccfEndpointCert,
            CcfEndpointSkipTlsVerify = ccfEndpointSkipTlsVerify,
            CcfRecoverySvcEndpoint = this.config[SettingName.CcfRecoverySvcEndpoint],
            CcfRecoverySvcEndpointCert = ccfRecoverySvcEndpointCert,
            CcfRecoverySvcEndpointSkipTlsVerify = ccfRecoverySvcEndpointSkipTlsVerify
        };

        wsConfig.Attestation = await Attestation.GenerateRsaKeyPairAndReportAsync();
        return wsConfig;
    }

    private async Task<string?> GetCcfEndpointCertAsync()
    {
        var ccfEndpointCert = this.config[SettingName.CcfEndpointCert];
        if (!string.IsNullOrEmpty(ccfEndpointCert))
        {
            if (Path.Exists(ccfEndpointCert))
            {
                return await File.ReadAllTextAsync(ccfEndpointCert);
            }

            byte[] serviceCert = Convert.FromBase64String(ccfEndpointCert);
            return Encoding.UTF8.GetString(serviceCert);
        }

        return null;
    }

    private async Task<string?> GetCcfRecoverySvcEndpointCertAsync()
    {
        var cert = this.config[SettingName.CcfRecoverySvcEndpointCert];
        if (!string.IsNullOrEmpty(cert))
        {
            if (Path.Exists(cert))
            {
                return await File.ReadAllTextAsync(cert);
            }

            byte[] serviceCert = Convert.FromBase64String(cert);
            return Encoding.UTF8.GetString(serviceCert);
        }

        return null;
    }
}
