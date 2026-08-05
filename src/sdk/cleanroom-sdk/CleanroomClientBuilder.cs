// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ClientModel.Primitives;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AttestationClient;
using CoseUtils;
using Microsoft.Ccf.Client;
using Microsoft.Extensions.Logging;

using GeneratedClient =
    Azure.Cleanroom.Governance.Client.GovernanceClient;
using GeneratedOptions =
    Azure.Cleanroom.Governance.Client.GovernanceClientOptions;

namespace Azure.Cleanroom.Sdk;

#pragma warning disable SA1611 // Missing parameter documentation - pending scrub.
#pragma warning disable SA1615 // Element return value should be documented - pending scrub.

/// <summary>
/// Builder for creating <see cref="CleanroomClient"/> instances.
/// </summary>
/// <example>
/// <b>SNP attestation (generate at build time)</b>:
/// <code>
/// var client = await new CleanroomClientBuilder()
///     .WithBaseAddress("https://governance:8300")
///     .WithServiceCertificate(serviceCertPem)
///     .WithSnpAttestation()
///     .BuildAsync();
/// var secret = await client.GetSecretAsync("c1", "my-secret");
/// </code>
///
/// <b>SNP attestation from file</b>:
/// <code>
/// var client = await new CleanroomClientBuilder()
///     .WithBaseAddress("https://governance:8300")
///     .WithSnpAttestationFromFile("/path/to/report.json")
///     .BuildAsync();
/// </code>
///
/// <b>JWT auth</b>:
/// <code>
/// var client = await new CleanroomClientBuilder()
///     .WithBaseAddress("https://governance:8080")
///     .WithServiceCertificate(serviceCertPem)
///     .WithJwtCredentials(tokenCredential, scope)
///     .BuildAsync();
/// </code>
///
/// <b>Member (COSE) auth</b>:
/// <code>
/// var client = await new CleanroomClientBuilder()
///     .WithBaseAddress("https://governance:8080")
///     .WithServiceCertificate(serviceCertPem)
///     .WithJwtCredentials(tokenCredential, scope)
///     .WithMemberCredentials(signingCertPem, signingKeyPem)
///     .BuildAsync();
/// </code>
/// </example>
public class CleanroomClientBuilder
{
    private string? baseAddress;
    private string? serviceCertificatePem;
    private X509Certificate2? clientCertificate;
    private Azure.Core.TokenCredential? tokenCredential;
    private string? tokenScope;
    private AttestationCredentials? attestationCredentials;
    private MemberCredentials? memberCredentials;
    private string apiVersion = "2024-07-01";
    private TimeSpan timeout = TimeSpan.FromSeconds(30);
    private ILogger? logger;

    private bool skipTlsVerify;
    private Func<Task<string>>? certDiscoveryCallback;

    private Func<HttpMessageHandler, DelegatingHandler>?
        outerHandlerFactory;

    // Deferred attestation modes.
    private bool generateSnpAttestation;

    private string? snpAttestationFilePath;

    /// <summary>
    /// Sets the base address of the governance service.
    /// </summary>
    public CleanroomClientBuilder WithBaseAddress(
        string baseAddress)
    {
        this.baseAddress = baseAddress;
        return this;
    }

    /// <summary>
    /// Sets the service certificate in PEM format for TLS validation.
    /// </summary>
    public CleanroomClientBuilder WithServiceCertificate(
        string serviceCertPem)
    {
        this.serviceCertificatePem = serviceCertPem;
        return this;
    }

    /// <summary>
    /// Sets a client certificate for mTLS authentication.
    /// </summary>
    public CleanroomClientBuilder WithClientCertificate(
        X509Certificate2 certificate)
    {
        this.clientCertificate = certificate;
        return this;
    }

    /// <summary>
    /// Skips TLS server certificate verification. Use only for
    /// development and testing against local clusters.
    /// </summary>
    public CleanroomClientBuilder WithSkipTlsVerify()
    {
        this.skipTlsVerify = true;
        return this;
    }

    /// <summary>
    /// Enables automatic certificate renewal. When a TLS handshake
    /// fails, the <paramref name="certDownloader"/> is invoked to
    /// obtain a fresh service certificate PEM, and the request is
    /// retried once.
    /// </summary>
    /// <param name="certDownloader">
    /// Async function that returns the renewed service certificate
    /// in PEM format (e.g., from a CCF service cert discovery
    /// endpoint).
    /// </param>
    public CleanroomClientBuilder WithServiceCertDiscovery(
        Func<Task<string>> certDownloader)
    {
        this.certDiscoveryCallback = certDownloader;
        return this;
    }

    /// <summary>
    /// Adds an outer <see cref="DelegatingHandler"/> wrapper around
    /// the HTTP pipeline (e.g., for retry policies, logging, or
    /// metrics). The <paramref name="handlerFactory"/> is called once
    /// per pipeline (the SDK uses two: one for <c>app/*</c>, one for
    /// <c>gov/*</c>). The factory receives the inner handler and must
    /// return a <see cref="DelegatingHandler"/> wrapping it.
    /// </summary>
    /// <example>
    /// <code>
    /// // Using Polly for retry:
    /// builder.WithOuterHandler(inner =>
    ///     new PolicyHttpMessageHandler(retryPolicy)
    ///     { InnerHandler = inner });
    /// </code>
    /// </example>
    public CleanroomClientBuilder WithOuterHandler(
        Func<HttpMessageHandler, DelegatingHandler> handlerFactory)
    {
        this.outerHandlerFactory = handlerFactory;
        return this;
    }

    /// <summary>
    /// Configures JWT bearer token authentication. Adds a
    /// <see cref="JwtAuthPolicy"/> to the generated client's pipeline.
    /// </summary>
    public CleanroomClientBuilder WithJwtCredentials(
        Azure.Core.TokenCredential tokenCredential,
        string scope)
    {
        this.tokenCredential = tokenCredential;
        this.tokenScope = scope;
        return this;
    }

    /// <summary>
    /// Configures SNP attestation with auto-generation. At build time,
    /// generates an RSA keypair and fetches an attestation report from
    /// the TEE (CACI or CVM).
    /// </summary>
    /// <remarks>
    /// Must be running inside a confidential computing environment
    /// (CACI or CVM with SEV-SNP). Use
    /// <see cref="WithSnpAttestationFromFile"/> for development/testing.
    /// </remarks>
    public CleanroomClientBuilder WithSnpAttestation()
    {
        this.generateSnpAttestation = true;
        this.snpAttestationFilePath = null;
        return this;
    }

    /// <summary>
    /// Configures SNP attestation from a pre-generated file.
    /// Reads the <c>AttestationReportKey</c> JSON file containing
    /// publicKey, privateKey, and report.
    /// </summary>
    /// <param name="filePath">
    /// Path to the attestation report JSON file.
    /// </param>
    public CleanroomClientBuilder WithSnpAttestationFromFile(
        string filePath)
    {
        this.snpAttestationFilePath = filePath;
        this.generateSnpAttestation = false;
        return this;
    }

    /// <summary>
    /// Configures pre-built attestation credentials directly.
    /// </summary>
    public CleanroomClientBuilder WithAttestation(
        AttestationCredentials credentials)
    {
        this.attestationCredentials = credentials;
        return this;
    }

    /// <summary>
    /// Configures member credentials for COSE-signed governance
    /// operations (proposals, voting, state digests).
    /// </summary>
    public CleanroomClientBuilder WithMemberCredentials(
        string signingCertPem,
        string signingKeyPem,
        string? memberId = null)
    {
        this.memberCredentials = new MemberCredentials
        {
            SigningCertificatePem = signingCertPem,
            SigningKeyPem = signingKeyPem,
            MemberId = memberId
        };
        return this;
    }

    /// <summary>
    /// Sets the CCF API version.
    /// </summary>
    public CleanroomClientBuilder WithApiVersion(
        string apiVersion)
    {
        this.apiVersion = apiVersion;
        return this;
    }

    /// <summary>
    /// Sets the HTTP request timeout.
    /// </summary>
    public CleanroomClientBuilder WithTimeout(TimeSpan timeout)
    {
        this.timeout = timeout;
        return this;
    }

    /// <summary>
    /// Sets the logger.
    /// </summary>
    public CleanroomClientBuilder WithLogger(ILogger logger)
    {
        this.logger = logger;
        return this;
    }

    /// <summary>
    /// Builds the <see cref="CleanroomClient"/> asynchronously.
    /// Async because attestation generation
    /// (<see cref="WithSnpAttestation"/>) requires TEE calls.
    /// </summary>
    public async Task<CleanroomClient> BuildAsync()
    {
        if (string.IsNullOrEmpty(this.baseAddress))
        {
            throw new InvalidOperationException(
                "BaseAddress must be specified.");
        }

        // Step 1: Resolve attestation credentials if deferred.
        await this.ResolveAttestationCredentialsAsync();

        var endpoint = new Uri(this.baseAddress);

        // Step 1.5: When member credentials are provided but no
        // explicit client cert, derive one for mTLS authentication
        // on app/* endpoints.
        if (this.memberCredentials != null &&
            this.clientCertificate == null)
        {
            this.clientCertificate = X509Certificate2.CreateFromPem(
                this.memberCredentials.SigningCertificatePem,
                this.memberCredentials.SigningKeyPem);
        }

        // Step 2: Create shared TLS handler for CCF's self-signed
        // certs. Both the generated client and gov/* HttpClient use
        // the same SocketsHttpHandler (connection pool + TLS config).
        var tlsHandler = new CcfTlsHandler(
            this.logger,
            this.serviceCertificatePem,
            this.skipTlsVerify,
            this.clientCertificate);

        // Step 3: Resolve the HTTP handler for each pipeline.
        // When cert discovery is configured, wrap in an auto-renewal
        // DelegatingHandler. When an outer handler factory is set
        // (e.g., Polly retry), wrap that on top.
        HttpMessageHandler ResolveHandler()
        {
            HttpMessageHandler handler = tlsHandler.Handler;

            if (this.certDiscoveryCallback != null)
            {
                handler = new CertAutoRenewalHandler(
                    this.certDiscoveryCallback,
                    tlsHandler,
                    this.logger);
            }

            if (this.outerHandlerFactory != null)
            {
                var outer = this.outerHandlerFactory(handler);
                handler = outer;
            }

            return handler;
        }

        // When using auto-renewal, each pipeline gets its own
        // DelegatingHandler (DelegatingHandler can't be shared),
        // but they all update the same CcfTlsHandler cert roots.
        // Without auto-renewal, both share the SocketsHttpHandler.

        // Step 4: Configure generated client pipeline with custom
        // transport backed by the TLS handler.
        var options = new GeneratedOptions
        {
            Transport = new HttpClientPipelineTransport(
                new HttpClient(
                    ResolveHandler(),
                    disposeHandler: false)
                {
                    BaseAddress = endpoint,
                    Timeout = this.timeout
                })
        };

        if (this.tokenCredential != null && this.tokenScope != null)
        {
            options.AddPolicy(
                new JwtAuthPolicy(this.tokenCredential, this.tokenScope),
                PipelinePosition.PerCall);
        }

        // Step 5: Create the generated GovernanceClient.
        var generatedClient = new GeneratedClient(endpoint, options);

        // Step 6: Create attestation helper if credentials provided.
        AttestationHelper? attestation = null;
        if (this.attestationCredentials != null)
        {
            attestation = new AttestationHelper(
                this.attestationCredentials);
        }

        // Step 7: Create CCF client for gov/* endpoints. Always
        // created (read-only gov endpoints need no member auth).
        // When member credentials are provided, COSE signing key
        // and member ID are also set for write operations.
        var ccfOptions = new CcfClientOptions
        {
            Transport = new HttpClientPipelineTransport(
                new HttpClient(
                    ResolveHandler(),
                    disposeHandler: false)
                {
                    BaseAddress = endpoint,
                    Timeout = this.timeout
                })
        };

        var ccfClient = new CcfClient(endpoint, ccfOptions);

        CoseSignKey? coseSignKey = null;
        string? memberId = null;
        if (this.memberCredentials != null)
        {
            coseSignKey = new CoseSignKey(
                this.memberCredentials.SigningCertificatePem,
                this.memberCredentials.SigningKeyPem);

            // Compute member ID from certificate.
            if (!string.IsNullOrEmpty(
                this.memberCredentials.MemberId))
            {
                memberId = this.memberCredentials.MemberId;
            }
            else
            {
                using var cert = X509Certificate2.CreateFromPem(
                    coseSignKey.Certificate);
                memberId = cert.GetCertHashString(
                    HashAlgorithmName.SHA256).ToLower();
            }
        }

        // Step 8: Wrap in the facade.
        return new CleanroomClient(
            generatedClient,
            attestation,
            ccfClient,
            endpoint,
            coseSignKey,
            memberId,
            tlsHandler,
            this.logger);
    }

    /// <summary>
    /// Builds synchronously. Use <see cref="BuildAsync"/> if using
    /// <see cref="WithSnpAttestation"/> (requires async TEE calls).
    /// </summary>
    public CleanroomClient Build()
    {
        if (this.generateSnpAttestation)
        {
            throw new InvalidOperationException(
                "WithSnpAttestation() requires BuildAsync(). " +
                "Use BuildAsync() or WithSnpAttestationFromFile().");
        }

        return this.BuildAsync()
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();
    }

    private async Task ResolveAttestationCredentialsAsync()
    {
        if (this.attestationCredentials != null)
        {
            // Already configured directly.
            return;
        }

        if (this.generateSnpAttestation)
        {
            // Generate RSA keypair + fetch attestation from TEE.
            var reportKey =
                await Attestation.GenerateRsaKeyPairAndReportAsync();

            this.attestationCredentials = new AttestationCredentials
            {
                PrivateKeyPem = reportKey.PrivateKey,
                PublicKeyPem = reportKey.PublicKey,
                Report = reportKey.Report
            };
        }
        else if (!string.IsNullOrEmpty(this.snpAttestationFilePath))
        {
            // Read from file.
            var json = await File.ReadAllTextAsync(
                this.snpAttestationFilePath);
            var reportKey =
                JsonSerializer.Deserialize<AttestationReportKey>(json)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialize attestation from " +
                    $"'{this.snpAttestationFilePath}'.");

            this.attestationCredentials = new AttestationCredentials
            {
                PrivateKeyPem = reportKey.PrivateKey,
                PublicKeyPem = reportKey.PublicKey,
                Report = reportKey.Report
            };
        }
        else if (this.tokenCredential != null)
        {
            // JWT mode: generate ephemeral keypair for response
            // encryption on dual-auth endpoints. No attestation report.
            var keyPair = Attestation.GenerateRsaKeyPair();
            this.attestationCredentials = new AttestationCredentials
            {
                PrivateKeyPem = keyPair.PrivateKey,
                PublicKeyPem = keyPair.PublicKey,
                Report = null
            };
        }
    }
}