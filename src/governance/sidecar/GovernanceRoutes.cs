// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public class Routes
{
    private IConfiguration config;
    private string? configPathPrefix;

    public Routes(IConfiguration config)
    {
        this.config = config;
        this.configPathPrefix =
            this.SanitizePrefix(this.config[SettingName.CcrGovApiPathPrefix]);
    }

    public string Secrets(WebContext webContext, string secretId)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/secrets/{secretId}";
    }

    public string Documents(WebContext webContext, string documentId)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/acceptedDocuments/{documentId}";
    }

    public string Events(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/events";
    }

    public string OAuthToken(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/oauth/token";
    }

    public string IsCAEnabled(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/ca/isEnabled";
    }

    public string GenerateEndorsedCert(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/ca/generateEndorsedCert";
    }

    public string ConsentCheckExecution(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/consentcheck/execution";
    }

    public string ConsentCheckLogging(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/consentcheck/logging";
    }

    public string ConsentCheckTelemetry(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/consentcheck/telemetry";
    }

    private string? SanitizePrefix(string? value)
    {
        if (value != null)
        {
            value = value.TrimStart('/');
        }

        return value;
    }

    private string GetPathPrefix(string? pathPrefix)
    {
        if (string.IsNullOrEmpty(pathPrefix) && string.IsNullOrEmpty(this.configPathPrefix))
        {
            throw new Exception($"{SettingName.CcrGovApiPathPrefix} setting must be specified.");
        }

        return (this.SanitizePrefix(pathPrefix) ?? this.configPathPrefix)!.TrimEnd('/');
    }
}
