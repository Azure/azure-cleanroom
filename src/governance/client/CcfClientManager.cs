// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using CoseUtils;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

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
        var cfg = this.ccfConfig ?? Defaults.CcfConfiguration;
        var ws = new WorkspaceConfiguration()
        {
            CcfEndpoint = cfg?.CcfEndpoint,
            ServiceCert = cfg?.ServiceCert,
            ServiceCertDiscovery = cfg?.CertLocator?.Model
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
            TryInitializeGovFromEnvironment(this.logger);
        }

        if (Defaults.SigningConfiguration == null)
        {
            throw new Exception(
                "Invoke /configure first to setup signing configuration.");
        }

        var client = this.InitializeClient(EndpointAuthType.Gov);
        return Task.FromResult(client);
    }

    public HttpClient GetAppClient()
    {
        if (Defaults.HttpsClientCert == null &&
            Defaults.JwtTokenConfiguration == null)
        {
            TryInitializeAppFromEnvironment(this.logger);
        }

        if (Defaults.HttpsClientCert == null &&
            Defaults.JwtTokenConfiguration == null)
        {
            throw new Exception(
                "Client cert or user token credential is mandatory. " +
                "Invoke /configure to setup the user authentication " +
                "configuration.");
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

    private static void TryInitializeGovFromEnvironment(ILogger logger)
    {
        if (Defaults.SigningConfiguration != null)
        {
            return;
        }

        var (signingCert, signingKey) = ReadSigningCredsFromEnv();
        if (signingCert == null || signingKey == null)
        {
            return;
        }

        logger.LogInformation(
            "Initializing gov auth from environment variables.");
        var coseSignKey = new CoseSignKey(signingCert, signingKey);
        SetGovAuthDefaults(coseSignKey);
        EnsureCcfDefaultsFromEnvironment(logger);
    }

    private static void TryInitializeAppFromEnvironment(ILogger logger)
    {
        if (Defaults.HttpsClientCert != null || Defaults.JwtTokenConfiguration != null)
        {
            return;
        }

        // Check if local identity auth is requested via environment.
        string? useLocalIdentity =
            Environment.GetEnvironmentVariable("CGS_CLIENT_USE_LOCAL_IDENTITY");
        if (!string.IsNullOrEmpty(useLocalIdentity))
        {
            TryInitializeLocalIdpFromEnvironment(logger);
            return;
        }

        var (signingCert, signingKey) = ReadSigningCredsFromEnv();
        if (signingCert == null || signingKey == null)
        {
            return;
        }

        logger.LogInformation(
            "Initializing app auth from environment variables.");
        X509Certificate2 httpsClientCert =
            X509Certificate2.CreateFromPem(signingCert, signingKey);
        SetAppAuthDefaults(httpsClientCert);
        EnsureCcfDefaultsFromEnvironment(logger);
    }

    private static void TryInitializeLocalIdpFromEnvironment(ILogger logger)
    {
        string? identityUrl = Environment.GetEnvironmentVariable("LOCAL_IDP_ENDPOINT");
        if (string.IsNullOrEmpty(identityUrl))
        {
            logger.LogWarning(
                "CGS_CLIENT_USE_LOCAL_IDENTITY is set but LOCAL_IDP_ENDPOINT is not configured.");
            return;
        }

        logger.LogInformation(
            "Initializing app auth from environment using local " +
            "identity. Endpoint: {Endpoint}",
            identityUrl);
        var scope = "https://does.not.matter";
        var creds = new LocalIdpCachedTokenCredential(identityUrl);
        var sharableClaims = new JsonObject
        {
            ["oid"] = "local-idp-oid",
            ["preferred_username"] = "local-idp-user",
            ["sub"] = "local-idp-sub",
            ["tid"] = "local-idp-tid"
        };
        SetAppAuthDefaults(creds, scope, sharableClaims, AuthMode.LocalIdp);
        EnsureCcfDefaultsFromEnvironment(logger);
    }

    private static (string? Cert, string? Key) ReadSigningCredsFromEnv()
    {
        string? signingCert = ReadFromPathOrValue(
            "CGS_CLIENT_SIGNING_CERT_PATH", "CGS_CLIENT_SIGNING_CERT");
        string? signingKey = ReadFromPathOrValue(
            "CGS_CLIENT_SIGNING_KEY_PATH", "CGS_CLIENT_SIGNING_KEY");
        return (signingCert, signingKey);
    }

    private static void EnsureCcfDefaultsFromEnvironment(ILogger logger)
    {
        if (Defaults.CcfConfiguration != null)
        {
            return;
        }

        string? ccfEndpoint =
            Environment.GetEnvironmentVariable("CGS_CLIENT_CCF_ENDPOINT");
        if (string.IsNullOrEmpty(ccfEndpoint))
        {
            return;
        }

        string? serviceCertPem = ReadFromPathOrValue(
            "CGS_CLIENT_SERVICE_CERT_PATH", "CGS_CLIENT_SERVICE_CERT");

        logger.LogInformation(
            "Setting CCF defaults from environment. " +
            "Endpoint: {Endpoint}",
            ccfEndpoint);
        SetCcfDefaults(ccfEndpoint, serviceCertPem, certLocator: null);
    }

    private static string? ReadFromPathOrValue(string pathEnvVar, string valueEnvVar)
    {
        string? path =
            Environment.GetEnvironmentVariable(pathEnvVar);
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        string? value =
            Environment.GetEnvironmentVariable(valueEnvVar);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private HttpClient InitializeClient(EndpointAuthType epType)
    {
        var config = this.ccfConfig ?? Defaults.CcfConfiguration;
        if (config == null)
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
        if (config.CertLocator != null && config.ServiceCert != null)
        {
            certValidationHandler = new AutoRenewingCertHandler(
                this.logger,
                config.CertLocator,
                GetServerCertValidationHandler(config.ServiceCert),
                onRenewal: (serviceCertPem) =>
                    Defaults.CcfConfiguration!.ServiceCert = serviceCertPem);
        }
        else
        {
            certValidationHandler = GetServerCertValidationHandler(config.ServiceCert);
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
            BaseAddress = new Uri(config.CcfEndpoint)
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