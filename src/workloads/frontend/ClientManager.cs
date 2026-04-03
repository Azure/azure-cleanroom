// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Controllers;
using FrontendSvc.CGSClient;
using FrontendSvc.MembershipManagerClient;
using FrontendSvc.Models;
using Polly;

namespace FrontendSvc;

public static class HttpClientExtensions
{
    public static async Task WaitForContainerUpAsync(this HttpClient containerClient, ILogger logger)
    {
        var retryPolicy = Polly.Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                x => TimeSpan.FromSeconds(5));

        await retryPolicy.ExecuteAsync(
            async () =>
            {
                using HttpRequestMessage request = new(HttpMethod.Get, "/ready");
                using HttpResponseMessage response = await containerClient.SendAsync(request);
                await response.ValidateStatusCodeAsync(logger);
            });
    }
}

public class ClientManager
{
    private readonly ILogger logger;
    private readonly IHostEnvironment hostEnvironment;
    private readonly SemaphoreSlim credentialSemaphore = new(1, 1);
    private WorkspaceConfiguration wsConfig;
    private IConfiguration config;
    private string cgsEndpointName = "ccf-governance";

    private HttpClientManager httpClientManager;
    private ClientCertificateCredential? servicePrincipalCredential;

    public ClientManager(
        ILogger logger,
        IConfiguration config,
        IHostEnvironment hostEnvironment)
    {
        this.logger = logger;
        this.config = config;
        this.hostEnvironment = hostEnvironment;
        this.wsConfig = this.InitializeWsConfigFromEnvironment();
        this.httpClientManager = new HttpClientManager(this.logger);
    }

    public string CcrServiceCertPath => this.wsConfig.CcrServiceCertPath;

    public string ConsortiumManagerEndpoint => this.wsConfig.ConsortiumManagerEndpoint;

    public string MembershipManagerEndpoint => this.wsConfig.MembershipManagerEndpoint;

    public string AnalyticsWorkloadId => this.wsConfig.AnalyticsWorkloadId;

    public async Task<CollaborationDetails> GetCollaborationAsync(
        string idToken,
        string collaborationId)
    {
        this.logger.LogInformation(
            "Acquiring Collaboration details using id: {CollaborationId} from the MembershipMgr.",
            collaborationId);
        var membershipMgrClient = await this.GetMembershipManagerClientAsync();

        var consortiumDetails = await membershipMgrClient.GetCollaborationAsync(
            idToken,
            collaborationId,
            this.logger);

        var containerHttpClient = this.httpClientManager.GetOrAddClient(
            this.wsConfig.CgsClientEndpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert: consortiumDetails.ConsortiumServiceCertificatePem,
            this.cgsEndpointName);

        await HttpClientExtensions.WaitForContainerUpAsync(containerHttpClient, this.logger);

        return new CollaborationDetails
        {
            ConsortiumEndpoint = consortiumDetails.ConsortiumEndpoint,
            ConsortiumServiceCertificatePem = consortiumDetails.ConsortiumServiceCertificatePem,
            UserToken = idToken,
            CgsClient = containerHttpClient,
            AnalyticsWorkloadId = this.wsConfig.AnalyticsWorkloadId,
        };
    }

    public async Task<HttpClient> GetAnalyticsClientAsync(
        string idToken,
        string collaborationId,
        ILogger logger)
    {
        logger.LogInformation("Acquiring Analytics client using collaboration details.");
        var governanceClient = await this.GetCollaborationAsync(
            idToken,
            collaborationId);

        var deploymentInfo = await governanceClient.GetDeploymentInfoAsync(
            this.wsConfig.AnalyticsWorkloadId,
            logger);

        if (deploymentInfo.Data == null)
        {
            throw new ApiException(
                HttpStatusCode.NotFound,
                new ODataError(
                    "DeploymentInfoNotFound",
                    $"No deployment info found for contract {this.wsConfig.AnalyticsWorkloadId}"));
        }

        var dataElement = (JsonElement)deploymentInfo.Data;
        if (!dataElement.TryGetProperty("url", out var urlElement))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    "DeploymentEndpointNotFound",
                    "Deployment endpoint URL not found in deployment information"));
        }

        string analyticsEndpoint = urlElement.GetString()!;
        logger.LogInformation($"Using deployment endpoint: {analyticsEndpoint}");

        // This check is to ensure that the analytics endpoint URL has a trailing slash
        // for proper URI construction. HttpClient combines the base URL with relative paths
        // (e.g., "queries/{id}/runs/{runId}"). Without a trailing slash, the last segment
        // of the base URL path gets replaced instead of appended. We cannot add leading slashes
        // to relative paths because a leading slash will make the path absolute (starts from root),
        // ignoring the base URL path entirely.
        if (!analyticsEndpoint.EndsWith("/"))
        {
            analyticsEndpoint += "/";
        }

        // TODO Skip TLS cert validation for analytics agent connections for now.
        // Agent endpoint is anyways voted in the CGS, so using the same only for analytics calls.
        var client = this.httpClientManager.GetOrAddClient(
            analyticsEndpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            skipTlsVerify: true,
            endpointName: "analytics-agent");

        return client;
    }

    public async Task<HttpClient> GetConsortiumManagerClientAsync()
    {
        this.logger.LogInformation("Acquiring Consortium Manager client with the App token.");

        string? serviceCert = null;
        if (!this.hostEnvironment.IsDevelopment())
        {
            serviceCert = await this.GetConsortiumManagerServiceCertAsync();
        }

        var token = await this.GetFirstPartyTokenAsync();
        var endpoint = this.wsConfig.ConsortiumManagerEndpoint;
        if (!endpoint.EndsWith("/"))
        {
            endpoint += "/";
        }

        var client = this.httpClientManager.GetOrAddClient(
            endpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert: serviceCert,
            endpointName: "consortium-manager");

        return this.CreateAuthenticatedClient(client, token);
    }

    public async Task<HttpClient> GetMembershipManagerClientAsync()
    {
        this.logger.LogInformation("Acquiring Membership Manager client with the App token.");
        var token = await this.GetFirstPartyTokenAsync();
        var endpoint = this.wsConfig.MembershipManagerEndpoint;
        if (!endpoint.EndsWith("/"))
        {
            endpoint += "/";
        }

        var client = this.httpClientManager.GetOrAddClient(
            endpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert: null,
            endpointName: "membership-manager");

        return this.CreateAuthenticatedClient(client, token);
    }

    /// <summary>
    /// Gets or creates a cached HttpClient for direct communication with CCF.
    /// </summary>
    /// <param name="collaborationDetails">
    /// Collaboration details containing consortium endpoint and service certificate.
    /// </param>
    /// <returns>A cached HttpClient configured for CCF communication.</returns>
    public HttpClient GetCcfClient(CollaborationDetails collaborationDetails)
    {
        var ccfEndpoint = collaborationDetails.ConsortiumEndpoint;
        if (!ccfEndpoint.StartsWith("http"))
        {
            ccfEndpoint = "https://" + ccfEndpoint;
        }

        return this.httpClientManager.GetOrAddClient(
            ccfEndpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert: collaborationDetails.ConsortiumServiceCertificatePem,
            endpointName: "ccf-client");
    }

    /// <summary>
    /// Gets or creates a cached HttpClient for a CCF recovery endpoint.
    /// </summary>
    /// <param name="endpoint">The endpoint URL.</param>
    /// <returns>A cached HttpClient configured with retry policies.</returns>
    public HttpClient GetCcfRecoveryClient(string endpoint)
    {
        return this.httpClientManager.GetOrAddClient(
            endpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointName: "ccf-recovery-client");
    }

    public void UpdateAnalyticsWorkloadId(string workloadId)
    {
        this.wsConfig.AnalyticsWorkloadId = workloadId;
    }

    private async Task<string> GetConsortiumManagerServiceCertAsync()
    {
        var insecureClient = this.httpClientManager.GetOrAddClient(
            this.wsConfig.ConsortiumManagerEndpoint,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert: null,
            endpointName: "consortium-manager-report",
            skipTlsVerify: true);

        using var response = await insecureClient.GetAsync("/report");
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var report = JsonSerializer.Deserialize<JsonElement>(content);
        var serviceCert = report.GetProperty("serviceCert").GetString()!;
        this.logger.LogInformation(
            "Retrieved consortium manager service certificate.");
        return serviceCert;
    }

    /// <summary>
    /// Creates a new HttpClient that wraps the given inner client and injects the
    /// Authorization header per-request.
    /// </summary>
    private HttpClient CreateAuthenticatedClient(HttpClient innerClient, string token)
    {
        var handler = new AuthorizationDelegatingHandler(token)
        {
            InnerHandler = new ForwardingHandler(innerClient)
        };

        return new HttpClient(handler)
        {
            BaseAddress = innerClient.BaseAddress
        };
    }

    private async Task<string> GetFirstPartyTokenAsync()
    {
        // In development, use DefaultAzureCredential which picks up the local
        // az login session. In production, use the service principal certificate
        // flow via ClientCertificateCredential.
        if (this.hostEnvironment.IsDevelopment())
        {
            this.logger.LogInformation(
                "Development environment detected, acquiring token via DefaultAzureCredential.");
            return await this.GetTokenViaDefaultCredentialAsync();
        }

        return await this.GetTokenFromAadAsync();
    }

    private async Task<string> GetTokenViaDefaultCredentialAsync()
    {
        var scope = this.wsConfig.FirstPartyAppTokenScope ?? string.Empty;
        var credential = new DefaultAzureCredential();
        var tokenRequestContext = new TokenRequestContext(scopes: new[] { scope });
        var token = await credential.GetTokenAsync(
            tokenRequestContext,
            CancellationToken.None);
        this.logger.LogInformation("Successfully acquired token via DefaultAzureCredential.");
        return token.Token;
    }

    private async Task<string> GetTokenFromAadAsync()
    {
        var credential = await this.GetServicePrincipalCredentialAsync();
        var scope = this.wsConfig.FirstPartyAppTokenScope ?? string.Empty;
        var tokenRequestContext = new TokenRequestContext(scopes: new[] { scope });
        var token = await credential.GetTokenAsync(
            tokenRequestContext,
            CancellationToken.None);
        this.logger.LogInformation($"Successfully acquired token from AAD.");

        return token.Token;
    }

    private async Task<ClientCertificateCredential> GetServicePrincipalCredentialAsync()
    {
        if (this.servicePrincipalCredential == null)
        {
            await this.credentialSemaphore.WaitAsync();
            try
            {
                if (this.servicePrincipalCredential == null)
                {
                    var certificate = await this.GetCertificateFromKeyVaultAsync();
                    this.servicePrincipalCredential = new ClientCertificateCredential(
                        this.wsConfig.TenantId,
                        this.wsConfig.FirstPartyAppId,
                        certificate,
                        new ClientCertificateCredentialOptions
                        {
                            SendCertificateChain = true
                        });
                }
            }
            catch (Exception ex)
            {
                this.logger.LogError(
                    ex,
                    "Failed to initialize Service Principal credential");
                throw;
            }
            finally
            {
                this.credentialSemaphore.Release();
            }
        }

        return this.servicePrincipalCredential;
    }

    private async Task<X509Certificate2> GetCertificateFromKeyVaultAsync()
    {
        var kvCredential = new DefaultAzureCredential();
        try
        {
            var certClient = new CertificateClient(
                new Uri(this.wsConfig.KeyVaultUrl), kvCredential);
            var certificateResponse = await certClient.DownloadCertificateAsync(
                this.wsConfig.FirstPartyAppCertificateName);

            return certificateResponse.Value;
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                "Failed to get certificate from Key Vault");
            throw;
        }
    }

    private WorkspaceConfiguration InitializeWsConfigFromEnvironment()
    {
        if (string.IsNullOrEmpty(this.config[SettingName.CgsClientEndpoint]))
        {
            throw new ArgumentException(
                $"{SettingName.CgsClientEndpoint} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.ConsortiumManagerEndpoint]))
        {
            throw new ArgumentException(
                $"{SettingName.ConsortiumManagerEndpoint} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.MembershipManagerEndpoint]))
        {
            throw new ArgumentException(
                $"{SettingName.MembershipManagerEndpoint} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.FirstPartyAppId]))
        {
            throw new ArgumentException(
                $"{SettingName.FirstPartyAppId} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.KeyVaultUrl]))
        {
            throw new ArgumentException(
                $"{SettingName.KeyVaultUrl} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.FirstPartyAppCertificateName]))
        {
            throw new ArgumentException(
                $"{SettingName.FirstPartyAppCertificateName} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.FirstPartyAppTokenScope]))
        {
            this.logger.LogWarning(
                $"{SettingName.FirstPartyAppTokenScope} setting is not specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.TenantId]))
        {
            throw new ArgumentException(
                $"{SettingName.TenantId} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.AnalyticsWorkloadId]))
        {
            throw new ArgumentException(
                $"{SettingName.AnalyticsWorkloadId} setting must be specified.");
        }

        if (string.IsNullOrEmpty(this.config[SettingName.CcrServiceCertPath]))
        {
            throw new ArgumentException(
                $"{SettingName.CcrServiceCertPath} setting must be specified.");
        }

        var wsConfig = new WorkspaceConfiguration();
        wsConfig.CgsClientEndpoint = this.config[SettingName.CgsClientEndpoint]!;
        wsConfig.ConsortiumManagerEndpoint =
            this.config[SettingName.ConsortiumManagerEndpoint]!;
        wsConfig.MembershipManagerEndpoint =
            this.config[SettingName.MembershipManagerEndpoint]!;
        wsConfig.FirstPartyAppId =
            this.config[SettingName.FirstPartyAppId]!;
        wsConfig.KeyVaultUrl =
            this.config[SettingName.KeyVaultUrl]!;
        wsConfig.FirstPartyAppCertificateName =
            this.config[SettingName.FirstPartyAppCertificateName]!;
        wsConfig.FirstPartyAppTokenScope =
            this.config[SettingName.FirstPartyAppTokenScope];
        wsConfig.TenantId =
            this.config[SettingName.TenantId]!;
        wsConfig.AnalyticsWorkloadId =
            this.config[SettingName.AnalyticsWorkloadId]!;
        wsConfig.CcrServiceCertPath =
            this.config[SettingName.CcrServiceCertPath]!;

        return wsConfig;
    }
}

/// <summary>
/// A DelegatingHandler that adds an Authorization Bearer header to every outgoing request.
/// </summary>
internal class AuthorizationDelegatingHandler : DelegatingHandler
{
    private readonly string token;

    public AuthorizationDelegatingHandler(string token)
    {
        this.token = token;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", this.token);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// A handler that forwards requests to an existing HttpClient, preserving its
/// configured message handler pipeline (retry policies, TLS settings, etc.).
/// Because the outer HttpClient.SendAsync marks the HttpRequestMessage as "sent",
/// we must create a new HttpRequestMessage to forward to the inner HttpClient
/// to avoid the "The request message was already sent" error.
/// </summary>
internal class ForwardingHandler : HttpMessageHandler
{
    private readonly HttpClient innerClient;

    public ForwardingHandler(HttpClient innerClient)
    {
        this.innerClient = innerClient;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Clone the request because the outer HttpClient.SendAsync has already marked
        // the original request as "sent", and HttpClient.SendAsync on the inner client
        // will reject a request that has already been sent.
        var forwardedRequest = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
        };

        // Share the content reference (do not dispose forwardedRequest as it would
        // also dispose the shared Content).
        forwardedRequest.Content = request.Content;

        foreach (var header in request.Headers)
        {
            forwardedRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in request.Options)
        {
            forwardedRequest.Options.TryAdd(option.Key, option.Value);
        }

        return await this.innerClient.SendAsync(forwardedRequest, cancellationToken);
    }
}
