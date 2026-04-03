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
        return $"{prefix}/memberdocuments/accepted/{documentId}";
    }

    public string UserDocuments(WebContext webContext, string documentId)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/userdocuments/accepted/{documentId}";
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

    public string Sign(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/signing/sign";
    }

    public string DelegateCleanRoomPolicy(
        WebContext webContext,
        string delegateType,
        string delegateId)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/cleanroompolicy/delegates/{delegateType}/{delegateId}";
    }

    public string GenerateEndorsedCert(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/ca/generateEndorsedCert";
    }

    public string ConsentCheckExecution(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/runtimeoptions/execution/status";
    }

    public string ConsentCheckLogging(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/runtimeoptions/logging/status";
    }

    public string ConsentCheckTelemetry(WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/runtimeoptions/telemetry/status";
    }

    public string ConsentCheckUserDocumentExecution(string documentId, WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/userdocuments/{documentId}/runtimeoptions/execution/status";
    }

    public string ConsentCheckUserDocumentTelemetry(string documentId, WebContext webContext)
    {
        var prefix = this.GetPathPrefix(webContext.GovernanceApiPathPrefix);
        return $"{prefix}/userdocuments/{documentId}/runtimeoptions/telemetry/status";
    }

    public string IsActiveUser(WebContext webContext)
    {
        // Note that this endpoint is not in the context of a contract.
        return $"/app/users/identities/self/status";
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
