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
        return $"{prefix}/acceptedMemberDocuments/{documentId}";
    }

    public string UserDocuments(WebContext webContext, string documentId)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/acceptedUserDocuments/{documentId}";
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

    public string TokenSubjectCleanRoomPolicy(WebContext webContext, string subjectName)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/oauth/federation/subjects/{subjectName}/cleanroompolicy";
    }

    public string SecretCleanRoomPolicy(WebContext webContext, string secretId)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/secrets/{secretId}/cleanroompolicy";
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

    public string ConsentCheckUserDocumentExecution(string documentId, WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/userdocuments/{documentId}/consentcheck/execution";
    }

    public string ConsentCheckUserDocumentTelemetry(string documentId, WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/userdocuments/{documentId}/consentcheck/telemetry";
    }

    public string IsActiveUser(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/users/isactive";
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
