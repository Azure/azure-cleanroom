// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using AttestationClient;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CcfConsortiumMgr.Auth;

internal class AuthConfigHandler
{
    private readonly List<AuthConfig> authConfigs;
    private readonly Dictionary<string, ConfigurationManager<OpenIdConnectConfiguration>>
        openIdConfigMap = new();

    public AuthConfigHandler(ILogger logger, IConfiguration configuration)
    {
        this.authConfigs =
            JsonSerializer.Deserialize<List<AuthConfig>>(
                configuration[SettingName.AuthConfigs]!) ??
            new List<AuthConfig>();
        if (this.authConfigs.Count == 0)
        {
            if (Attestation.IsSnpCACI())
            {
                throw new ArgumentException("No auth configuration specified.");
            }

            this.IsNoAuthMode = true;
        }

        foreach (AuthConfig authConfig in this.authConfigs)
        {
            ConfigurationManager<OpenIdConnectConfiguration>? openIdConfig;
            if (!this.openIdConfigMap.TryGetValue(authConfig.OpenIdConfigEndpoint, out openIdConfig))
            {
                openIdConfig = GetOpenIdConfigManager(authConfig.OpenIdConfigEndpoint);
                this.openIdConfigMap[authConfig.OpenIdConfigEndpoint] = openIdConfig;
            }

            authConfig.SetOpenIdConfigManager(openIdConfig);
        }
    }

    public bool IsNoAuthMode { get; private set; }

    public AuthConfig? GetAuthConfig(string tenantId, string objectId)
    {
        return this.authConfigs.SingleOrDefault(x =>
            x.TenantId == tenantId &&
            x.ObjectId == objectId);
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> GetOpenIdConfigManager(
        string openIdConfigEndpoint)
    {
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            openIdConfigEndpoint,
            new OpenIdConnectConfigurationRetriever());
    }
}
