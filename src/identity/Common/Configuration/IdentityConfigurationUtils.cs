// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Identity.Configuration;

/// <summary>
/// Class that defines the utility methods for <see cref="IdentityConfiguration"/>.
/// </summary>
public static class IdentityConfigurationUtils
{
    /// <summary>
    /// Gets the hosting environment.
    /// </summary>
    internal static string HostingEnvironment { get; private set; } = "Production";

    /// <summary>
    /// Builds the configuration.
    /// </summary>
    /// <param name="configBuilder">The configuration builder.</param>
    /// <param name="hostingEnvironment">The hosting environment.</param>
    /// <param name="args">The arguments.</param>
    public static void BuildConfiguration(
        this IConfigurationManager configBuilder,
        IHostEnvironment hostingEnvironment,
        string[] args)
    {
        HostingEnvironment = hostingEnvironment.EnvironmentName;
        configBuilder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

        string identityConfig = Environment.GetEnvironmentVariable("IdentitySideCarArgs")!;

        // Load the app settings first, followed by overrides.
        // Note: Since the identities section in the configuration consists of arrays, if it is
        // specified in multiple places, say the app settings file and the environment variable,
        // it results in a partial override with the array indices being overwritten by whichever
        // gets loaded last.
        configBuilder.AddJsonFile(
            $"appsettings.{hostingEnvironment.EnvironmentName}.json",
            optional: false,
            reloadOnChange: true);

        if (!string.IsNullOrWhiteSpace(identityConfig))
        {
            var base64EncodedBytes = Convert.FromBase64String(identityConfig);
            var identityConfigStr = Encoding.UTF8.GetString(base64EncodedBytes);
            configBuilder.AddJsonStream(
                new MemoryStream(Encoding.UTF8.GetBytes(identityConfigStr)));
        }

        configBuilder.AddEnvironmentVariables();
        configBuilder.AddCommandLine(args);
    }

    /// <summary>
    /// Get an instance of <see cref="IdentityConfiguration"/> from app settings or environment
    /// variables.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The configuration associated with this setup.</returns>
    public static IdentityConfiguration GetIdentityConfiguration(this IConfiguration configuration)
    {
        var identityConfig = configuration.GetSection("Identities").Get<Identities>();

        // For development scenarios, we would like to use the credentials of the logged in user.
        // This can be achieved by leaving the Managed Identity section and the Application
        // Identity section empty.
        // However, IConfiguration is not great at handling empty arrays and these get null-ed
        // on fetching from an IConfiguration.
        // Working around this by manually setting lists to empty lists.
        if (identityConfig != null)
        {
            identityConfig.ApplicationIdentities ??= new List<ApplicationIdentity>();
            identityConfig.ManagedIdentities ??= new List<ManagedIdentity>();
        }

        var config = new IdentityConfiguration { Identities = identityConfig! };
        ValidateConfiguration(config);
        return config;
    }

    public static DiagnosticsConfiguration GetDiagnosticsConfiguration(
        this IConfiguration configuration)
    {
        return configuration.GetSection("Diagnostics").Get<DiagnosticsConfiguration>()!;
    }

    /// <summary>
    /// Validates the configuration.
    /// </summary>
    /// <param name="config">The identity configuration.</param>
    private static void ValidateConfiguration(IdentityConfiguration config)
    {
        if (config == null)
        {
            throw new ApiException(new ODataError(
                code: "InvalidConfiguration",
                message: $"The configuration specified is invalid - " +
                $"{nameof(IdentityConfiguration)} is null."));
        }

        foreach (var managedIdentity in config.Identities.ManagedIdentities)
        {
            if (string.IsNullOrEmpty(managedIdentity.ClientId))
            {
                throw new ApiException(new ODataError(
                    code: "InvalidConfiguration",
                    message: $"The configuration specified is invalid - " +
                    $"Managed Identity has an empty client ID."));
            }
        }

        foreach (var applicationIdentity in config.Identities.ApplicationIdentities)
        {
            if (string.IsNullOrEmpty(applicationIdentity.ClientId))
            {
                throw new ApiException(new ODataError(
                    code: "InvalidConfiguration",
                    message: $"The configuration specified is invalid - " +
                    $"Application Identity has an empty client ID."));
            }

            if (applicationIdentity.Credential == null)
            {
                throw new ApiException(new ODataError(
                    code: "InvalidConfiguration",
                    message: $"The configuration specified is invalid - " +
                    $"Application Identity must have credential details specified, client ID: " +
                    $"{applicationIdentity.ClientId}."));
            }

            if (applicationIdentity.Credential.CredentialType == CredentialType.FederatedCredential)
            {
                if (applicationIdentity.Credential.SecretConfiguration != null)
                {
                    throw new ApiException(new ODataError(
                        code: "InvalidConfiguration",
                        message: $"The configuration specified is invalid - " +
                        $"{nameof(SecretConfiguration)} must not be specified for a Federated " +
                        $"credential, client ID: {applicationIdentity.ClientId}."));
                }

                if (applicationIdentity.Credential.FederationConfiguration == null)
                {
                    throw new ApiException(new ODataError(
                        code: "InvalidConfiguration",
                        message: $"The configuration specified is invalid - " +
                        $"A federated credential must have an associated configuration, " +
                        $"client ID: {applicationIdentity.ClientId}."));
                }
            }

            if (applicationIdentity.Credential.CredentialType !=
                CredentialType.FederatedCredential)
            {
                if (applicationIdentity.Credential.SecretConfiguration == null)
                {
                    throw new ApiException(new ODataError(
                        code: "InvalidConfiguration",
                        message: $"The configuration specified is invalid - " +
                        $"Application Identity must have a {nameof(SecretConfiguration)} " +
                        $"specified, client ID: {applicationIdentity.ClientId}."));
                }

                if (applicationIdentity.Credential.SecretConfiguration.SecretStore == null)
                {
                    throw new ApiException(new ODataError(
                        code: "InvalidConfiguration",
                        message: $"The configuration specified is invalid - " +
                        $"Application Identity is missing a {nameof(SecretStore)}" +
                        $", client ID: {applicationIdentity.ClientId}."));
                }
            }
        }
    }
}
