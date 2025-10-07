// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
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
                    this.ccfAppClient = await this.InitializeClient();
                }
            }
            finally
            {
                this.semaphore.Release();
            }
        }
    }

    private async Task<HttpClient> InitializeClient()
    {
        var ccrgovEndpoint = new Uri(this.wsConfig.CcrgovEndpoint);

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
        if (this.wsConfig.ServiceCertLocator != null)
        {
            var initialServiceCertPem =
                await this.wsConfig.ServiceCertLocator.DownloadServiceCertificatePem();

            certValidationHandler = new AutoRenewingCertHandler(
                this.logger,
                this.wsConfig.ServiceCertLocator,
                GetServerCertValidationHandler(initialServiceCertPem),
                onRenewal: (serviceCertPem) => { });
        }
        else
        {
            certValidationHandler = GetServerCertValidationHandler(this.wsConfig.ServiceCert);
        }

        var retryPolicyHandler = new PolicyHttpMessageHandler(
            HttpRetries.Policies.DefaultRetryPolicy(this.logger))
        {
            InnerHandler = certValidationHandler
        };
        var client = new HttpClient(retryPolicyHandler)
        {
            BaseAddress = ccrgovEndpoint
        };
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

                string model = this.wsConfig.ServiceCertLocator?.Model != null ?
                    JsonSerializer.Serialize(this.wsConfig.ServiceCertLocator.Model) : string.Empty;
                this.logger.LogInformation($"ccr-governance initialized with " +
                    $"ccrgovEndpoint: '{this.wsConfig.CcrgovEndpoint}', " +
                    $"ccrgovEndpointPathPrefix: '{this.config[SettingName.CcrGovApiPathPrefix]}', " +
                    $"serviceCert: '{this.wsConfig.ServiceCert}', " +
                    $"serviceCertDiscoveryModel: '{model}'.");
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
