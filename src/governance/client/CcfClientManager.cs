// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.Extensions.Http;

namespace Controllers;

public class CcfClientManager
{
    private const string Version = "2024-07-01";
    private static readonly CcfClientManagerDefaults Defaults = new();
    private readonly CcfConfiguration? ccfConfig;
    private readonly ILogger logger;
    private readonly IHttpContextAccessor httpContextAccessor;

    public CcfClientManager(
        ILogger logger,
        string? ccfEndpoint,
        string? serviceCertPem,
        CcfServiceCertLocator? serviceCertDoc,
        string? authMode,
        IHttpContextAccessor httpContextAccessor)
    {
        this.logger = logger;
        this.ccfConfig = string.IsNullOrEmpty(ccfEndpoint) ?
            Defaults.CcfConfiguration :
            new CcfConfiguration(ccfEndpoint, serviceCertPem, serviceCertDoc);
        if (!string.IsNullOrEmpty(authMode))
        {
            Defaults.JwtTokenConfiguration = new JwtTokenConfiguration(authMode);
        }

        this.httpContextAccessor = httpContextAccessor;
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
        CcfTokenCredential tokenCredential,
        string scope,
        JsonObject tokenClaimsCopy,
        string authMode)
    {
        Defaults.JwtTokenConfiguration =
            new JwtTokenConfiguration(scope, tokenCredential, tokenClaimsCopy, authMode);
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

        if (Defaults.JwtTokenConfiguration != null)
        {
            ws.IsUser = true;
            ws.JwtClaims = Defaults.JwtTokenConfiguration.TokenClaims;
            ws.AuthMode = Defaults.JwtTokenConfiguration.AuthMode;
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
        if (Defaults.HttpsClientCert == null && Defaults.JwtTokenConfiguration == null)
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
        DelegatingHandler authenticationHandler;
        if (epType == EndpointAuthType.App && Defaults.JwtTokenConfiguration != null)
        {
            if (Defaults.JwtTokenConfiguration.AuthMode == AuthMode.FromAuthHeader)
            {
                authenticationHandler = new ForwardAuthHeaderDelegatingHandler(
                    this.httpContextAccessor);
            }
            else
            {
                // jwt based auth.
                authenticationHandler = new TokenCredentialDelegatingHandler(
                    Defaults.JwtTokenConfiguration.TokenCredential,
                    Defaults.JwtTokenConfiguration.TokenCredentialScope);
            }

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
}

public class CcfClientManagerDefaults
{
    public CcfConfiguration? CcfConfiguration { get; set; }

    public SigningConfiguration? SigningConfiguration { get; set; }

    public JwtTokenConfiguration? JwtTokenConfiguration { get; set; }

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

public class JwtTokenConfiguration
{
    public JwtTokenConfiguration(
        string tokenCredentialScope,
        CcfTokenCredential tokenCredential,
        JsonObject tokenClaims,
        string authMode)
    {
        this.TokenCredentialScope = tokenCredentialScope;
        this.TokenCredential = tokenCredential;
        this.TokenClaims = tokenClaims;
        this.AuthMode = authMode;
    }

    public JwtTokenConfiguration(string authMode)
    {
        if (authMode != "FromAuthHeader")
        {
            throw new ArgumentException(
                $"Only FromAuthHeader auth mode is supported in this ctor. Input was '{authMode}'.");
        }

        this.TokenCredentialScope = null!;
        this.TokenCredential = null!;
        this.TokenClaims = null!;
        this.AuthMode = authMode;
    }

    public string TokenCredentialScope { get; set; }

    public CcfTokenCredential TokenCredential { get; set; }

    public JsonObject? TokenClaims { get; set; }

    public string AuthMode { get; set; }
}