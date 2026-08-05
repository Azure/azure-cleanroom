// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CoseUtils;
using Microsoft.Ccf.Client;
using Microsoft.Extensions.Logging;

using GeneratedClient =
    Azure.Cleanroom.Governance.Client.GovernanceClient;

namespace Azure.Cleanroom.Sdk;

/// <summary>
/// Unified governance client implementing both <see cref="ICleanroomClient"/>
/// (caller-facing, clean signatures) and the wire-level client
/// (wire-level, auto-generated from TypeSpec).
/// </summary>
/// <remarks>
/// <para>
/// <b>Callers should use <see cref="ICleanroomClient"/></b> — it hides
/// attestation types (<c>SnpEvidence</c>, <c>Encrypt</c>, <c>Sign</c>)
/// and auto-decrypts responses.
/// </para>
/// <para>
/// Auth is handled via:
/// </para>
/// <list type="bullet">
/// <item><description>
///   <b>JWT/cert</b>: <see cref="JwtAuthPolicy"/> injected into the
///   CGS client pipeline.
/// </description></item>
/// <item><description>
///   <b>Attestation</b>: Auto-injected by <see cref="ICleanroomClient"/>
///   methods, or explicit in the wire-level client methods.
/// </description></item>
/// <item><description>
///   <b>COSE signing</b>: <c>gov/*</c> write endpoints use explicit
///   COSE Sign1 envelopes built at each call site via
///   <see cref="CoseUtils.Cose.CreateGovCoseSign1Message"/>.
/// </description></item>
/// </list>
/// </remarks>
public partial class CleanroomClient :
    ICleanroomClient
{
    private readonly GeneratedClient generatedClient;
    private readonly AttestationHelper? attestation;
    private readonly CcfClient ccfClient;
    private readonly Uri endpoint;
    private readonly CoseSignKey? coseSignKey;
    private readonly string? memberId;
    private readonly CcfTlsHandler? tlsHandler;
    private readonly ILogger? logger;
    private bool disposed;

    internal CleanroomClient(
        GeneratedClient generatedClient,
        AttestationHelper? attestation,
        CcfClient ccfClient,
        Uri endpoint,
        CoseSignKey? coseSignKey,
        string? memberId,
        CcfTlsHandler? tlsHandler,
        ILogger? logger)
    {
        this.generatedClient = generatedClient;
        this.attestation = attestation;
        this.ccfClient = ccfClient;
        this.endpoint = endpoint;
        this.coseSignKey = coseSignKey;
        this.memberId = memberId;
        this.tlsHandler = tlsHandler;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public string? MemberId => this.memberId;

    /// <inheritdoc/>
    public Task<CleanroomClientCapabilities> GetCapabilitiesAsync()
    {
        var caps = CleanroomClientCapabilities.AppOperations;
        if (this.attestation != null)
        {
            caps |= CleanroomClientCapabilities.AttestationOperations;
        }

        if (this.coseSignKey != null)
        {
            caps |= CleanroomClientCapabilities.MemberOperations;
        }

        return Task.FromResult(caps);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!this.disposed)
        {
            this.tlsHandler?.Handler.Dispose();
            this.disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private void RequireAttestation()
    {
        if (this.attestation == null)
        {
            throw new InvalidOperationException(
                "This operation requires attestation credentials. " +
                "Configure WithAttestation() on the builder.");
        }
    }

    private void RequireMemberAuth()
    {
        if (this.coseSignKey == null || this.memberId == null)
        {
            throw new InvalidOperationException(
                "This operation requires member credentials. " +
                "Configure WithMemberCredentials() on the " +
                "builder.");
        }
    }
}