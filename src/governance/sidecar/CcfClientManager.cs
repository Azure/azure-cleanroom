// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AttestationClient;
using Azure.Core;
using Microsoft.Extensions.Http;

namespace Controllers;

public class CcfClientManager
{
    private readonly ILogger logger;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private WorkspaceConfiguration? wsConfigOnce;
    private HttpClient? ccfAppClientOnce;
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
        return await this.InitializeAppClient();
    }

    public async Task<WorkspaceConfiguration> GetWsConfig()
    {
        return await this.InitializeWsConfig();
    }

    private async Task<HttpClient> InitializeAppClient()
    {
        var wsConfig = await this.InitializeWsConfig();
        if (this.ccfAppClientOnce == null)
        {
            try
            {
                await this.semaphore.WaitAsync();
                if (this.ccfAppClientOnce == null)
                {
                    this.ccfAppClientOnce = await this.InitializeClient(wsConfig);
                }
            }
            finally
            {
                this.semaphore.Release();
            }
        }

        return this.ccfAppClientOnce;
    }

    private async Task<HttpClient> InitializeClient(WorkspaceConfiguration wsConfig)
    {
        var ccrgovEndpoint = new Uri(wsConfig.CcrgovEndpoint);

        ServerCertValidationHandler GetServerCertValidationHandler(string? serviceCertPem)
        {
            var serverCertValidationHandler =
                new ServerCertValidationHandler(
                    this.logger,
                    serviceCertPem,
                    endpointName: "ccr-governance");
            return serverCertValidationHandler;
        }

        HttpMessageHandler certValidationHandler;
        if (wsConfig.ServiceCertLocator != null)
        {
            var initialServiceCertPem =
                await wsConfig.ServiceCertLocator.DownloadServiceCertificatePem();

            certValidationHandler = new AutoRenewingCertHandler(
                this.logger,
                wsConfig.ServiceCertLocator,
                GetServerCertValidationHandler(initialServiceCertPem),
                onRenewal: (serviceCertPem) => { });
        }
        else
        {
            certValidationHandler = GetServerCertValidationHandler(wsConfig.ServiceCert);
        }

        var retryPolicyHandler = new PolicyHttpMessageHandler(
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));
        if (wsConfig.JwtTokenConfiguration != null)
        {
            // jwt based auth.
            var authenticationHandler = new TokenCredentialDelegatingHandler(
                wsConfig.JwtTokenConfiguration.TokenCredential,
                wsConfig.JwtTokenConfiguration.TokenCredentialScope);
            authenticationHandler.InnerHandler = certValidationHandler;
            retryPolicyHandler.InnerHandler = authenticationHandler;
        }
        else
        {
            retryPolicyHandler.InnerHandler = certValidationHandler;
        }

        var client = new HttpClient(retryPolicyHandler)
        {
            BaseAddress = ccrgovEndpoint
        };
        return client;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfig()
    {
        if (this.wsConfigOnce == null)
        {
            try
            {
                await this.semaphore.WaitAsync();
                if (this.wsConfigOnce == null)
                {
                    var authMode = this.config[SettingName.CcrGovAuthMode];
                    if (string.IsNullOrEmpty(authMode))
                    {
                        authMode = AuthMode.SnpAttestation;
                    }

                    if (authMode == AuthMode.SnpAttestation)
                    {
                        if (string.IsNullOrEmpty(this.config[SettingName.AttestationReport]))
                        {
                            this.wsConfigOnce = await this.InitializeWsConfigFetchAttestation();
                        }
                        else
                        {
                            this.wsConfigOnce = await this.InitializeWsConfigFromEnvironment();
                        }
                    }
                    else if (authMode == AuthMode.AzureLogin)
                    {
                        var scope = "https://management.azure.com/.default";
                        var ctx = new TokenRequestContext(new string[] { scope });
                        var creds = new DefaultAzureCcfTokenCredential();
                        this.wsConfigOnce = await this.InitializeWsConfigJwtConfiguration(
                            scope,
                            creds);
                    }
                    else if (authMode == AuthMode.LocalIdp)
                    {
                        string identityUrl = this.config["LOCAL_IDP_ENDPOINT"]!;
                        var scope = "https://does.not.matter";
                        var creds = new LocalIdpCachedTokenCredential(identityUrl);
                        this.wsConfigOnce = await this.InitializeWsConfigJwtConfiguration(
                            scope,
                            creds);
                    }
                    else
                    {
                        throw new ArgumentException(
                            $"Unsupported AuthMode: '{this.config[SettingName.CcrGovAuthMode]}'.");
                    }

                    this.wsConfigOnce.AuthMode = authMode;
                }

                string model = this.wsConfigOnce.ServiceCertLocator?.Model != null ?
                    JsonSerializer.Serialize(this.wsConfigOnce.ServiceCertLocator.Model)
                    : string.Empty;
                this.logger.LogInformation($"ccr-governance initialized with " +
                    $"ccrgovEndpoint: '{this.wsConfigOnce.CcrgovEndpoint}', " +
                    $"ccrgovEndpointPathPrefix: '{this.config[SettingName.CcrGovApiPathPrefix]}', " +
                    $"serviceCert: '{this.wsConfigOnce.ServiceCert}', " +
                    $"serviceCertDiscoveryModel: '{model}'.");
            }
            finally
            {
                this.semaphore.Release();
            }
        }

        return this.wsConfigOnce;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigFromEnvironment()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcrGovEndpoint]))
        {
            throw new ArgumentException("ccrgovEndpoint environment variable must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.AttestationReport]))
        {
            throw new ArgumentException(
                $"{SettingName.AttestationReport} setting must be specified.");
        }

        string? serviceCert = await this.GetServiceCertAsync();
        var serviceCertDiscovery = this.GetServiceCertDiscoveryModel();

        if (!string.IsNullOrEmpty(serviceCert) && serviceCertDiscovery != null)
        {
            throw new ArgumentException(
                $"Both {SettingName.ServiceCert} and " +
                $"{SettingName.ServiceCertDiscoveryEndpoint} cannot be specified.");
        }

        CcfServiceCertLocator? certLocator = null;
        if (serviceCertDiscovery != null)
        {
            certLocator = new CcfServiceCertLocator(this.logger, serviceCertDiscovery);
        }

        var wsConfig = new WorkspaceConfiguration
        {
            CcrgovEndpoint = this.config[SettingName.CcrGovEndpoint]!,
            ServiceCert = serviceCert,
            ServiceCertLocator = certLocator
        };

        var content = await File.ReadAllTextAsync(this.config[SettingName.AttestationReport]!);
        var report = JsonSerializer.Deserialize<AttestationReportKey>(content)!;
        wsConfig.KeyPair = new KeyPair(report.PublicKey, report.PrivateKey);
        wsConfig.Report = report.Report;
        return wsConfig;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigFetchAttestation()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcrGovEndpoint]))
        {
            throw new ArgumentException("ccrgovEndpoint environment variable must be specified.");
        }

        string? serviceCert = await this.GetServiceCertAsync();
        var serviceCertDiscovery = this.GetServiceCertDiscoveryModel();

        if (string.IsNullOrEmpty(serviceCert) && serviceCertDiscovery == null)
        {
            throw new ArgumentException(
                $"Either {SettingName.ServiceCert} or " +
                $"{SettingName.ServiceCertDiscoveryEndpoint} must be specified.");
        }

        if (!string.IsNullOrEmpty(serviceCert) && serviceCertDiscovery != null)
        {
            throw new ArgumentException(
                $"Both {SettingName.ServiceCert} and " +
                $"{SettingName.ServiceCertDiscoveryEndpoint} cannot be specified.");
        }

        CcfServiceCertLocator? certLocator = null;
        if (serviceCertDiscovery != null)
        {
            certLocator = new CcfServiceCertLocator(this.logger, serviceCertDiscovery);
        }

        var wsConfig = new WorkspaceConfiguration
        {
            CcrgovEndpoint = this.config[SettingName.CcrGovEndpoint]!,
            ServiceCert = serviceCert,
            ServiceCertLocator = certLocator
        };

        var keyPairAndReport = await Attestation.GenerateRsaKeyPairAndReportAsync();
        wsConfig.KeyPair = new KeyPair(keyPairAndReport.PublicKey, keyPairAndReport.PrivateKey);
        wsConfig.Report = keyPairAndReport.Report;
        return wsConfig;
    }

    private async Task<WorkspaceConfiguration> InitializeWsConfigJwtConfiguration(
        string tokenCredentialScope,
        CcfTokenCredential tokenCredential)
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CcrGovEndpoint]))
        {
            throw new ArgumentException("ccrgovEndpoint environment variable must be specified.");
        }

        string? serviceCert = await this.GetServiceCertAsync();
        var serviceCertDiscovery = this.GetServiceCertDiscoveryModel();

        if (string.IsNullOrEmpty(serviceCert) && serviceCertDiscovery == null)
        {
            throw new ArgumentException(
                $"Either {SettingName.ServiceCert} or " +
                $"{SettingName.ServiceCertDiscoveryEndpoint} must be specified.");
        }

        if (!string.IsNullOrEmpty(serviceCert) && serviceCertDiscovery != null)
        {
            throw new ArgumentException(
                $"Both {SettingName.ServiceCert} and " +
                $"{SettingName.ServiceCertDiscoveryEndpoint} cannot be specified.");
        }

        CcfServiceCertLocator? certLocator = null;
        if (serviceCertDiscovery != null)
        {
            certLocator = new CcfServiceCertLocator(this.logger, serviceCertDiscovery);
        }

        var wsConfig = new WorkspaceConfiguration
        {
            CcrgovEndpoint = this.config[SettingName.CcrGovEndpoint]!,
            ServiceCert = serviceCert,
            ServiceCertLocator = certLocator
        };

        wsConfig.JwtTokenConfiguration = new JwtTokenConfiguration(
            tokenCredentialScope,
            tokenCredential);
        wsConfig.KeyPair = Attestation.GenerateRsaKeyPair();
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

    private CcfServiceCertDiscoveryModel? GetServiceCertDiscoveryModel()
    {
        var ep = this.config[SettingName.ServiceCertDiscoveryEndpoint];
        if (!string.IsNullOrEmpty(ep))
        {
            var hostData = this.config[SettingName.ServiceCertDiscoverySnpHostData];
            if (string.IsNullOrEmpty(hostData))
            {
                throw new ArgumentException(
                    $"{SettingName.ServiceCertDiscoverySnpHostData} setting must be specified.");
            }

            var cd = this.config[SettingName.ServiceCertDiscoveryConstitutionDigest];
            var jd = this.config[SettingName.ServiceCertDiscoveryJsappBundleDigest];
            _ = bool.TryParse(
                this.config[SettingName.ServiceCertDiscoverySkipDigestCheck],
                out var skipDigestCheck);
            if (!skipDigestCheck && string.IsNullOrEmpty(cd))
            {
                throw new ArgumentException(
                    $"{SettingName.ServiceCertDiscoveryConstitutionDigest} setting must be " +
                    $"specified.");
            }

            if (!skipDigestCheck && string.IsNullOrEmpty(jd))
            {
                throw new ArgumentException(
                    $"{SettingName.ServiceCertDiscoveryJsappBundleDigest} setting must be " +
                    $"specified.");
            }

            return new CcfServiceCertDiscoveryModel
            {
                CertificateDiscoveryEndpoint = ep,
                HostData = [hostData],
                SkipDigestCheck = skipDigestCheck,
                ConstitutionDigest = cd,
                JsAppBundleDigest = jd,
            };
        }

        return null;
    }
}