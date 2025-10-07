// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Azure.Core;
using CoseUtils;
using Microsoft.Extensions.Http;

namespace Controllers;

public class CcfClientManager
{
    private const string Version = "2024-07-01";
    private static readonly CcfClientManagerDefaults Defaults = new();
    private readonly CcfConfiguration? ccfConfig;
    private readonly ILogger logger;

    public CcfClientManager(
        ILogger logger,
        string? ccfEndpoint,
        string? serviceCertPem,
        CcfServiceCertLocator? serviceCertDoc)
    {
        this.logger = logger;
        this.ccfConfig = string.IsNullOrEmpty(ccfEndpoint) ?
            Defaults.CcfConfiguration :
            new CcfConfiguration(ccfEndpoint, serviceCertPem, serviceCertDoc);
    }

    private enum EndpointAuthType
    {
        Gov,
        App,
        NoAuth
    }

    public static void SetGovAuthDefaults(CoseSignKey coseSignKey)
    {
        using var cert = X509Certificate2.CreateFromPem(coseSignKey.Certificate);
        Defaults.SigningConfiguration = new SigningConfiguration(
            coseSignKey,
            cert.GetCertHashString(HashAlgorithmName.SHA256).ToLower());
    }

    public static void SetAppAuthDefaults(X509Certificate2 httpsClientCert)
    {
        Defaults.HttpsClientCert = httpsClientCert;
    }

    public static void SetAppAuthDefaults(
        CcfTokenCredential userTokenCredential,
        string scope,
        JsonObject userTokenClaimsCopy,
        string authMode)
    {
        Defaults.UserTokenConfiguration =
            new UserTokenConfiguration(scope, userTokenCredential, userTokenClaimsCopy, authMode);
    }

    public static void SetCcfDefaults(
        string ccfEndpoint,
        string? serviceCertPem,
        CcfServiceCertLocator? certLocator)
    {
        if (certLocator != null && string.IsNullOrEmpty(serviceCertPem))
        {
            // One expects an initial service cert to be supplied to kick off communication with
            // CCF until the need arises to redownload the cert.
            throw new Exception("serviceCertPem must be supplied along with serviceCertDoc");
        }

        Defaults.CcfConfiguration =
            new CcfConfiguration(ccfEndpoint, serviceCertPem, certLocator);
    }

    public WorkspaceConfiguration GetWsConfig()
    {
        var ws = new WorkspaceConfiguration()
        {
            CcfEndpoint = this.ccfConfig?.CcfEndpoint,
            ServiceCert = this.ccfConfig?.ServiceCert,
            ServiceCertDiscovery = this.ccfConfig?.CertLocator?.Model
        };

        if (Defaults.SigningConfiguration != null)
        {
            ws.SigningCert = Defaults.SigningConfiguration.SignKey.Certificate;
            ws.SigningKey = Defaults.SigningConfiguration.SignKey.PrivateKey;
            ws.SigningCertId = Defaults.SigningConfiguration.SignKey.SigningCertId?.ToString();
            ws.MemberId = Defaults.SigningConfiguration.MemberId;
        }

        if (Defaults.UserTokenConfiguration != null)
        {
            ws.IsUser = true;
            ws.UserTokenClaims = Defaults.UserTokenConfiguration.UserTokenClaims;
            ws.AuthMode = Defaults.UserTokenConfiguration.AuthMode;
        }

        return ws;
    }

    public string GetMemberId()
    {
        return Defaults.SigningConfiguration?.MemberId ??
            throw new Exception("signing configuration not set");
    }

    public CoseSignKey GetCoseSignKey()
    {
        return Defaults.SigningConfiguration?.SignKey ??
            throw new Exception("signing configuration not set");
    }

    public Task<HttpClient> GetGovClient()
    {
        if (Defaults.SigningConfiguration == null)
        {
            throw new Exception("Invoke /configure first to setup signing configuration.");
        }

        var client = this.InitializeClient(EndpointAuthType.Gov);
        return Task.FromResult(client);
    }

    public HttpClient GetAppClient()
    {
        if (Defaults.HttpsClientCert == null && Defaults.UserTokenConfiguration == null)
        {
            throw new Exception("Client cert or user token credential is mandatory. Invoke " +
                "/configure to setup the user authentication configuration.");
        }

        var client = this.InitializeClient(EndpointAuthType.App);
        return client;
    }

    public HttpClient GetNoAuthClient()
    {
        var client = this.InitializeClient(EndpointAuthType.NoAuth);
        return client;
    }

    public string GetGovApiVersion()
    {
        return Version;
    }

    private HttpClient InitializeClient(EndpointAuthType epType)
    {
        if (this.ccfConfig == null)
        {
            throw new Exception("CCF endpoint is mandatory.");
        }

        ServerCertValidationHandler GetServerCertValidationHandler(string? serviceCertPem)
        {
            X509Certificate2? clientCert = null;
            if (epType == EndpointAuthType.App && Defaults.HttpsClientCert != null)
            {
                // client cert based auth.
                clientCert = Defaults.HttpsClientCert;
            }

            var serverCertValidationHandler =
                new ServerCertValidationHandler(
                    this.logger,
                    serviceCertPem,
                    clientCert: clientCert,
                    endpointName: "cgs-client");

            return serverCertValidationHandler;
        }

        HttpMessageHandler certValidationHandler;
        if (this.ccfConfig.CertLocator != null && this.ccfConfig.ServiceCert != null)
        {
            certValidationHandler = new AutoRenewingCertHandler(
                this.logger,
                this.ccfConfig.CertLocator,
                GetServerCertValidationHandler(this.ccfConfig.ServiceCert),
                onRenewal: (serviceCertPem) =>
                    Defaults.CcfConfiguration!.ServiceCert = serviceCertPem);
        }
        else
        {
            certValidationHandler = GetServerCertValidationHandler(this.ccfConfig.ServiceCert);
        }

        // The chain is:
        // retryPolicyHandler ->
        //   [AuthenticationDelegatingHandler] ->
        //     certValidationHandler: [AutoRenewingCertHandler] -> ServerCertValidationHandler.
        var retryPolicyHandler = new PolicyHttpMessageHandler(
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));
        if (epType == EndpointAuthType.App && Defaults.UserTokenConfiguration != null)
        {
            // jwt based auth.
            var authenticationHandler = new AuthenticationDelegatingHandler(
                Defaults.UserTokenConfiguration.UserTokenCredential,
                Defaults.UserTokenConfiguration.UserTokenCredentialScope);
            authenticationHandler.InnerHandler = certValidationHandler;
            retryPolicyHandler.InnerHandler = authenticationHandler;
        }
        else
        {
            retryPolicyHandler.InnerHandler = certValidationHandler;
        }

        var client = new HttpClient(retryPolicyHandler)
        {
            BaseAddress = new Uri(this.ccfConfig.CcfEndpoint)
        };
        return client;
    }

    internal class AuthenticationDelegatingHandler
    : DelegatingHandler
    {
        private CcfTokenCredential tokenCredential;
        private string scope;

        public AuthenticationDelegatingHandler(CcfTokenCredential tokenCredential, string scope)
        {
            this.tokenCredential = tokenCredential;
            this.scope = scope;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var ctx = new TokenRequestContext(new string[] { this.scope });
            string token = await this.tokenCredential.GetTokenAsync(
                ctx,
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}

public class CcfClientManagerDefaults
{
    public CcfConfiguration? CcfConfiguration { get; set; }

    public SigningConfiguration? SigningConfiguration { get; set; }

    public UserTokenConfiguration? UserTokenConfiguration { get; set; }

    public X509Certificate2? HttpsClientCert { get; set; }
}

public class SigningConfiguration(CoseSignKey signKey, string memberId)
{
    public CoseSignKey SignKey { get; set; } = signKey;

    public string MemberId { get; set; } = memberId;
}

public class CcfConfiguration(
    string ccfEndpoint,
    string? serviceCert,
    CcfServiceCertLocator? certLocator)
{
    public string CcfEndpoint { get; set; } = ccfEndpoint;

    public string? ServiceCert { get; set; } = serviceCert;

    public CcfServiceCertLocator? CertLocator { get; set; } = certLocator;
}

public class UserTokenConfiguration(
    string userTokenCredentialScope,
    CcfTokenCredential userTokenCredential,
    JsonObject userTokenClaims,
    string authMode)
{
    public string UserTokenCredentialScope { get; set; } = userTokenCredentialScope;

    public CcfTokenCredential UserTokenCredential { get; set; } = userTokenCredential;

    public JsonObject? UserTokenClaims { get; set; } = userTokenClaims;

    public string AuthMode { get; set; } = authMode;
}