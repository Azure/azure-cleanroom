// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CcfConsortiumMgr.Auth;

internal class AuthConfig : IJsonOnDeserialized
{
    private ConfigurationManager<OpenIdConnectConfiguration> openIdConfigManager = default!;

    [JsonPropertyName("openIdConfigEndpoint")]
    public string OpenIdConfigEndpoint { get; set; } = default!;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = default!;

    [JsonPropertyName("objectId")]
    public string ObjectId { get; set; } = default!;

    [JsonPropertyName("audience")]
    public string Audience { get; set; } = default!;

    [JsonPropertyName("validIssuers")]
    public List<string> ValidIssuers { get; set; } = default!;

    public void SetOpenIdConfigManager(ConfigurationManager<OpenIdConnectConfiguration> manager)
    {
        this.openIdConfigManager = manager;
    }

    public async Task<ICollection<SecurityKey>> GetSigningKeys()
    {
        // NOTE: ConfigurationManager does auto-refresh of the OpenID configuration on a
        // periodic basis.
        OpenIdConnectConfiguration config =
            await this.openIdConfigManager.GetConfigurationAsync();
        return config.SigningKeys;
    }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (string.IsNullOrEmpty(this.OpenIdConfigEndpoint))
        {
            throw new ArgumentNullException($"{nameof(this.OpenIdConfigEndpoint)}");
        }

        if (string.IsNullOrEmpty(this.TenantId))
        {
            throw new ArgumentNullException($"{nameof(this.TenantId)}");
        }

        if (string.IsNullOrEmpty(this.ObjectId))
        {
            throw new ArgumentNullException($"{nameof(this.ObjectId)}");
        }

        if (string.IsNullOrEmpty(this.Audience))
        {
            throw new ArgumentNullException($"{nameof(this.Audience)}");
        }

        if (this.ValidIssuers == null || this.ValidIssuers.Count == 0)
        {
            throw new ArgumentNullException($"{nameof(this.ValidIssuers)}");
        }
    }
}
