// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace Controllers;

[ApiController]
public class WorkspacesController : ClientControllerBase
{
    private readonly IConfiguration config;

    public WorkspacesController(
        ILogger<WorkspacesController> logger,
        IConfiguration config,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
        this.config = config;
    }

    [HttpGet("/ready")]
    public IActionResult Ready()
    {
        return this.Ok(new JsonObject
        {
            ["status"] = "up"
        });
    }

    [HttpPost("/configure")]
    public async Task<IActionResult> SetWorkspaceConfig(
        [FromForm] WorkspaceConfigurationModel model)
    {
        this.Logger.LogInformation(
            $"/configure invoked with following inputs:\n" +
            $"ccfEndpoint: '{model.CcfEndpoint}'\n" +
            $"serviceCertPemFile: {model.ServiceCertPemFile != null}\n" +
            $"signingCertId: '{model.SigningCertId}'\n" +
            $"signingCertPemFile: {model.SigningCertPemFile != null}\n" +
            $"signingKeyPemFile: {model.SigningKeyPemFile != null}\n" +
            $"authMode: '{model.AuthMode}'\n" +
            $"serviceCertDiscovery: '{model.ServiceCertDiscovery}'");

        if (model.SigningCertPemFile == null && string.IsNullOrEmpty(model.SigningCertId) &&
            string.IsNullOrEmpty(model.AuthMode))
        {
            return this.BadRequest(
                "Either SigningCertPemFile/SigningCertId/AuthMode must be specified");
        }

        if (model.SigningCertPemFile != null && !string.IsNullOrEmpty(model.SigningCertId))
        {
            return this.BadRequest(
                "Only one of SigningCertPemFile or SigningCertId must be specified");
        }

        if (!string.IsNullOrEmpty(model.AuthMode))
        {
            if (model.SigningCertPemFile != null || !string.IsNullOrEmpty(model.SigningCertId))
            {
                return this.BadRequest(
                    "Only one of AuthMode/SigningCertPemFile/SigningCertId must " +
                    "be specified");
            }
        }

        string ccfEndpoint = string.Empty;
        if (!string.IsNullOrEmpty(model.CcfEndpoint))
        {
            ccfEndpoint = model.CcfEndpoint.Trim();
            try
            {
                _ = new Uri(ccfEndpoint);
            }
            catch (Exception e)
            {
                return this.BadRequest($"Invalid ccfEndpoint value '{ccfEndpoint}': {e.Message}.");
            }
        }

        string? serviceCertPem = null;
        if (model.ServiceCertPemFile != null)
        {
            using (var reader3 = new StreamReader(model.ServiceCertPemFile.OpenReadStream()))
            {
                serviceCertPem = await reader3.ReadToEndAsync();
            }
        }

        CcfServiceCertLocator? certLocator = null;
        if (!string.IsNullOrEmpty(model.ServiceCertDiscovery))
        {
            if (serviceCertPem != null)
            {
                return this.BadRequest($"A service cert PEM cannot be specified along " +
                    $"with serviceCertDiscovery.");
            }

            var discoveryModel = JsonSerializer.Deserialize<CcfServiceCertDiscoveryModel>(
                model.ServiceCertDiscovery)!;

            var url = discoveryModel.CertificateDiscoveryEndpoint.Trim();
            try
            {
                _ = new Uri(url);
            }
            catch (Exception e)
            {
                return this.BadRequest(
                    $"Invalid serviceCertDiscovery.certificateDiscoveryEndpoint " +
                    $"value '{url}': {e.Message}.");
            }

            if (discoveryModel.HostData == null || !discoveryModel.HostData.Any())
            {
                return this.BadRequest($"serviceCertDiscovery.hostData must be specified.");
            }

            if (discoveryModel.SkipDigestCheck)
            {
                if (!string.IsNullOrEmpty(discoveryModel.ConstitutionDigest))
                {
                    return this.BadRequest(
                        $"serviceCertDiscovery.constitutionDigest cannot be specified along with " +
                        $"serviceCertDiscovery.skipDigestCheck as true.");
                }

                if (!string.IsNullOrEmpty(discoveryModel.JsAppBundleDigest))
                {
                    return this.BadRequest(
                        $"serviceCertDiscovery.jsAppBundleDigest cannot be specified along with " +
                        $"serviceCertDiscovery.skipDigestCheck as true.");
                }
            }
            else
            {
                if (string.IsNullOrEmpty(discoveryModel.ConstitutionDigest))
                {
                    return this.BadRequest(
                        $"serviceCertDiscovery.constitutionDigest must be specified.");
                }

                if (string.IsNullOrEmpty(discoveryModel.JsAppBundleDigest))
                {
                    return this.BadRequest(
                        $"serviceCertDiscovery.jsAppBundleDigest must be specified.");
                }
            }

            certLocator = new CcfServiceCertLocator(this.Logger, discoveryModel);
            serviceCertPem = await certLocator.DownloadServiceCertificatePem();
        }

        if (!string.IsNullOrEmpty(model.AuthMode))
        {
            // App authentication uses JWT authentication.
            if (model.AuthMode == "AzureLogin")
            {
                // Request a token to extract details that are useful to show in CLI/UI experiences.
                var scope = "https://management.core.windows.net";
                var ctx = new TokenRequestContext(new string[] { scope });
                var creds = new DefaultAzureCcfTokenCredential();
                string token = await creds.GetTokenAsync(
                    ctx,
                    CancellationToken.None);
                var parts = token.Split(".");
                var sharableClaims = CopySharableClaims(
                    JsonSerializer.Deserialize<JsonObject>(Base64UrlEncoder.Decode(parts[1]))!);
                CcfClientManager.SetAppAuthDefaults(creds, scope, sharableClaims, model.AuthMode);

                static JsonObject CopySharableClaims(JsonObject claims)
                {
                    return new JsonObject
                    {
                        ["oid"] = claims["oid"]?.ToString(),
                        ["unique_name"] = claims["unique_name"]?.ToString(),
                        ["name"] = claims["name"]?.ToString(),
                        ["family_name"] = claims["family_name"]?.ToString(),
                        ["given_name"] = claims["given_name"]?.ToString(),
                        ["idtyp"] = claims["idtyp"]?.ToString(),
                        ["sub"] = claims["sub"]?.ToString(),
                        ["upn"] = claims["upn"]?.ToString(),
                        ["tid"] = claims["tid"]?.ToString()
                    };
                }
            }
            else if (model.AuthMode == "MsLogin")
            {
                var scope = "User.Read";
                var ctx = new TokenRequestContext(new string[] { scope });
                var creds = new MsalCachedCcfTokenCredential(this.config["MSAL_TOKEN_CACHE_DIR"]);
                string token = await creds.GetTokenAsync(ctx, CancellationToken.None);
                var parts = token.Split(".");
                var sharableClaims = CopySharableClaims(
                    JsonSerializer.Deserialize<JsonObject>(Base64UrlEncoder.Decode(parts[1]))!);
                CcfClientManager.SetAppAuthDefaults(creds, scope, sharableClaims, model.AuthMode);

                static JsonObject CopySharableClaims(JsonObject claims)
                {
                    return new JsonObject
                    {
                        ["oid"] = claims["oid"]?.ToString(),
                        ["preferred_username"] = claims["preferred_username"]?.ToString(),
                        ["name"] = claims["name"]?.ToString(),
                        ["sub"] = claims["sub"]?.ToString(),
                        ["tid"] = claims["tid"]?.ToString()
                    };
                }
            }
            else if (model.AuthMode == "LocalIdp")
            {
                string identityUrl = this.config["LOCAL_IDP_ENDPOINT"]!;

                var scope = "https://does.not.matter";
                var ctx = new TokenRequestContext(new string[] { scope });
                var creds = new LocalIdpCachedTokenCredential(identityUrl);
                string token = await creds.GetTokenAsync(
                    ctx,
                    CancellationToken.None);
                var parts = token.Split(".");
                var sharableClaims = CopySharableClaims(
                    JsonSerializer.Deserialize<JsonObject>(Base64UrlEncoder.Decode(parts[1]))!);
                CcfClientManager.SetAppAuthDefaults(creds, scope, sharableClaims, model.AuthMode);

                static JsonObject CopySharableClaims(JsonObject claims)
                {
                    return new JsonObject
                    {
                        ["oid"] = claims["oid"]?.ToString(),
                        ["sub"] = claims["sub"]?.ToString(),
                        ["tid"] = claims["tid"]?.ToString()
                    };
                }
            }
            else
            {
                return this.BadRequest($"Invalid AuthMode value '{model.AuthMode}'.");
            }
        }
        else
        {
            CoseSignKey coseSignKey;
            X509Certificate2 httpsClientCert;
            if (model.SigningCertPemFile != null)
            {
                if (model.SigningCertPemFile.Length <= 0)
                {
                    return this.BadRequest("No signing cert file was uploaded.");
                }

                if (model.SigningKeyPemFile == null || model.SigningKeyPemFile.Length <= 0)
                {
                    return this.BadRequest("No signing key file was uploaded.");
                }

                string signingCert;
                using var reader = new StreamReader(model.SigningCertPemFile.OpenReadStream());
                signingCert = await reader.ReadToEndAsync();

                string signingKey;
                using var reader2 = new StreamReader(model.SigningKeyPemFile.OpenReadStream());
                signingKey = await reader2.ReadToEndAsync();

                coseSignKey = new CoseSignKey(signingCert, signingKey);
                httpsClientCert = X509Certificate2.CreateFromPem(signingCert, signingKey);
            }
            else
            {
                Uri signingCertId;
                try
                {
                    signingCertId = new Uri(model.SigningCertId!);
                }
                catch (Exception e)
                {
                    return this.BadRequest($"Invalid signingKid value: {e.Message}.");
                }

                var creds = new DefaultAzureCredential();
                coseSignKey = await CoseSignKey.FromKeyVault(signingCertId, creds);

                // Download the full cert along with private key for HTTPS client auth.
                var akvEndpoint = "https://" + signingCertId.Host;
                var certClient = new CertificateClient(new Uri(akvEndpoint), creds);

                // certificates/{name} or certificates/{name}/{version}
                var parts = signingCertId.AbsolutePath.Split(
                    "/",
                    StringSplitOptions.RemoveEmptyEntries);
                string certName = parts[1];
                string? version = parts.Length == 3 ? parts[2] : null;
                httpsClientCert = await certClient.DownloadCertificateAsync(certName, version);
            }

            // Governance endpoint uses Cose signed messages for member auth.
            CcfClientManager.SetGovAuthDefaults(coseSignKey);

            // App authentication uses member_cert authentication policy which uses HTTPS client cert
            // based authentication. So we need access to the member cert and private key for setting
            // up HTTPS client cert auth.
            CcfClientManager.SetAppAuthDefaults(httpsClientCert);
        }

        // Set workspace configuration values only if the CCF endpoint is specified as part of the
        // configure call. This serves as a default value when running in single CCF client mode.
        if (!string.IsNullOrEmpty(ccfEndpoint))
        {
            CcfClientManager.SetCcfDefaults(ccfEndpoint, serviceCertPem, certLocator);
        }

        return this.Ok("Workspace details configured successfully.");
    }

    [HttpGet("/identity/accessToken")]
    public async Task<IActionResult> GetAccessToken()
    {
        var wsConfig = this.CcfClientManager.GetWsConfig();
        if (wsConfig == null)
        {
            return this.BadRequest("Client has not yet been configured.");
        }

        if (string.IsNullOrEmpty(wsConfig.AuthMode))
        {
            return this.BadRequest("Client is not configured to use JWT authentication.");
        }

        // App authentication uses JWT authentication.
        string token;
        if (wsConfig.AuthMode == "AzureLogin")
        {
            // Request a token to return.
            var scope = "https://management.core.windows.net";
            var ctx = new TokenRequestContext(new string[] { scope });
            var creds = new DefaultAzureCcfTokenCredential();
            token = await creds.GetTokenAsync(ctx, CancellationToken.None);
        }
        else if (wsConfig.AuthMode == "MsLogin")
        {
            var scope = "User.Read";
            var ctx = new TokenRequestContext(new string[] { scope });
            var creds = new MsalCachedCcfTokenCredential(this.config["MSAL_TOKEN_CACHE_DIR"]);
            token = await creds.GetTokenAsync(ctx, CancellationToken.None);
        }
        else if (wsConfig.AuthMode == "LocalIdp")
        {
            string identityUrl = this.config["LOCAL_IDP_ENDPOINT"]!;
            var scope = "https://does.not.matter";
            var ctx = new TokenRequestContext(new string[] { scope });
            var creds = new LocalIdpCachedTokenCredential(identityUrl);
            token = await creds.GetTokenAsync(ctx, CancellationToken.None);
        }
        else
        {
            return this.BadRequest($"Unsupported AuthMode value '{wsConfig.AuthMode}'.");
        }

        return this.Ok(new JsonObject
        {
            ["accessToken"] = token
        });
    }

    [HttpGet("/show")]
    public async Task<IActionResult> Show([FromQuery] bool? signingKey = false)
    {
        WorkspaceConfiguration copy;
        var wsConfig = this.CcfClientManager.GetWsConfig();
        if (wsConfig != null)
        {
            copy =
                JsonSerializer.Deserialize<WorkspaceConfiguration>(
                    JsonSerializer.Serialize(wsConfig))!;
            if (!signingKey.GetValueOrDefault())
            {
                copy.SigningKey = "<redacted>";
            }

            if (copy.IsUser)
            {
                try
                {
                    // Get user/oid and if not null then set identifier value from it else
                    // set oid value from claim. And cleanup UI code to determine name to display.
                    var ccfClient = this.CcfClientManager.GetAppClient();
                    using HttpResponseMessage response = await ccfClient.GetAsync(
                        "app/users/identities");
                    await response.ValidateStatusCodeAsync(this.Logger);
                    var users = (await response.Content.ReadFromJsonAsync<UserIdentities>())!;
                    var oid = copy.UserTokenClaims!["oid"]!.ToString();
                    copy.Identifier = users.Value.Find(u => u.Id == oid)?.Data?.Identifier ?? oid;
                }
                catch (Exception e)
                {
                    this.Logger.LogError(e, "Failed to fetch users. Ignoring.");
                }
            }
            else
            {
                var ccfClient = this.CcfClientManager.GetNoAuthClient();
                try
                {
                    using HttpResponseMessage response = await ccfClient.GetAsync(
                    $"gov/service/members?api-version=" +
                    $"{this.CcfClientManager.GetGovApiVersion()}");
                    await response.ValidateStatusCodeAsync(this.Logger);
                    var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                    copy.Identifier = copy.MemberId;
                    copy.MemberData =
                        jsonResponse["value"]!.AsArray()
                        .FirstOrDefault(m => m!["memberId"]?.ToString() == copy.MemberId)?
                        ["memberData"]?.AsObject();
                    copy.Identifier = copy.MemberData?["identifier"]?.ToString() ?? copy.MemberId;
                }
                catch (Exception e)
                {
                    this.Logger.LogError(e, "Failed to fetch members. Ignoring.");
                }
            }
        }
        else
        {
            copy = new WorkspaceConfiguration();
        }

        copy.EnvironmentVariables = Environment.GetEnvironmentVariables();
        return this.Ok(copy);
    }

    [HttpGet("/constitution")]
    public async Task<IActionResult> GetConstitution()
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        var content = await ccfClient.GetConstitution(
            this.Logger,
            this.CcfClientManager.GetGovApiVersion());
        return this.Ok(content);
    }

    [HttpGet("/service/info")]
    public async Task<IActionResult> GetServiceInfo()
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        using HttpResponseMessage response =
            await ccfClient.GetAsync(
                $"gov/service/info?api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var content = (await response.Content.ReadAsStringAsync())!;
        return this.Ok(content);
    }

    [HttpGet("/jsapp/endpoints")]
    public async Task<IActionResult> GetJSAppEndpoints()
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        using HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-app?" +
            $"api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var content = (await response.Content.ReadAsStringAsync())!;
        return this.Ok(content);
    }

    [HttpGet("/jsapp/modules")]
    public async Task<IActionResult> JSAppModules()
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        using HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-modules?" +
            $"api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var modules = (await response.Content.ReadFromJsonAsync<JsonObject>())!;

        List<string> moduleNames = new();
        List<Task<string>> fetchModuleTasks = new();
        foreach (var item in modules["value"]!.AsArray().AsEnumerable())
        {
            var moduleName = item!.AsObject()["moduleName"]!.ToString();
            moduleNames.Add(moduleName);
        }

        // Sort the module names in alphabetical order so that we return the response ordered by
        // name.
        moduleNames = moduleNames.OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var moduleName in moduleNames)
        {
            var escapedString = Uri.EscapeDataString(moduleName);
            Task<string> fetchModuleTask = ccfClient.GetStringAsync(
            $"gov/service/javascript-modules/{escapedString}?" +
            $"api-version={this.CcfClientManager.GetGovApiVersion()}");
            fetchModuleTasks.Add(fetchModuleTask);
        }

        await Task.WhenAll(fetchModuleTasks);
        var modulesResponse = new JsonObject();
        for (int i = 0; i < moduleNames.Count; i++)
        {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            string content = (await fetchModuleTasks[i])!;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            modulesResponse[moduleNames[i]] = content;
        }

        return this.Ok(modulesResponse);
    }

    [HttpGet("/jsapp/modules/list")]
    public async Task<IActionResult> ListJSAppModules()
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        using HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-modules?" +
            $"api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var content = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return this.Ok(content);
    }

    [HttpGet("/jsapp/modules/{moduleName}")]
    public async Task<IActionResult> GetJSAppModule([FromRoute] string moduleName)
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        using HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-modules/{moduleName}?api-version=" +
            $"{this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var content = (await response.Content.ReadAsStringAsync())!;
        return this.Ok(content);
    }

    [HttpGet("/jsapp/bundle")]
    public async Task<IActionResult> GetJSAppBundle()
    {
        var ccfClient = this.CcfClientManager.GetNoAuthClient();
        var bundle = await ccfClient.GetJSAppBundle(
            this.Logger,
            this.CcfClientManager.GetGovApiVersion());
        return this.Ok(bundle);
    }

    public class ConfigView
    {
        public WorkspaceConfiguration? Config { get; set; } = default!;

        public System.Collections.IDictionary EnvironmentVariables { get; set; } = default!;
    }
}