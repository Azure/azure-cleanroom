// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using CoseUtils;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class WorkspacesController : ClientControllerBase
{
    public WorkspacesController(
        ILogger<WorkspacesController> logger,
        IHttpContextAccessor httpContextAccessor)
        : base(logger, httpContextAccessor)
    {
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
        if (model.SigningCertPemFile == null && string.IsNullOrEmpty(model.SigningCertId))
        {
            return this.BadRequest("Either SigningCertPemFile or SigningCertId must be specified");
        }

        if (model.SigningCertPemFile != null && !string.IsNullOrEmpty(model.SigningCertId))
        {
            return this.BadRequest(
                "Only one of SigningCertPemFile or SigningCertId must be specified");
        }

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

        string serviceCertPem = string.Empty;
        if (model.ServiceCertPemFile != null)
        {
            using (var reader3 = new StreamReader(model.ServiceCertPemFile.OpenReadStream()))
            {
                serviceCertPem = await reader3.ReadToEndAsync();
            }
        }

        // Governance endpoint uses Cose signed messages for member auth.
        CcfClientManager.SetGovAuthDefaults(coseSignKey);

        // App authentication uses member_cert authentication policy which uses HTTPS client cert
        // based authentication. So we need access to the member cert and private key for setting
        // up HTTPS client cert auth.
        CcfClientManager.SetAppAuthDefaults(httpsClientCert);

        // Set workspace configuration values only if the CCF endpoint is specified as part of the
        // configure call. This serves as a default value when running in single CCF client mode.
        if (!string.IsNullOrEmpty(ccfEndpoint))
        {
            CcfClientManager.SetCcfDefaults(ccfEndpoint, serviceCertPem);
        }

        return this.Ok("Workspace details configured successfully.");
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

            try
            {
                var ccfClient = await this.CcfClientManager.GetGovClient();
                using HttpResponseMessage response = await ccfClient.GetAsync(
                    $"gov/service/members?api-version={this.CcfClientManager.GetGovApiVersion()}");
                await response.ValidateStatusCodeAsync(this.Logger);
                var jsonResponse = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
                copy.MemberData =
                    jsonResponse["value"]!.AsArray()
                    .FirstOrDefault(m => m!["memberId"]?.ToString() == copy.MemberId)?
                    ["memberData"]!.AsObject();
            }
            catch (Exception e)
            {
                this.Logger.LogError(e, "Failed to fetch members. Ignoring.");
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
        var ccfClient = await this.CcfClientManager.GetGovClient();
        using HttpResponseMessage response =
            await ccfClient.GetAsync(
                $"gov/service/constitution?" +
                $"api-version={this.CcfClientManager.GetGovApiVersion()}");
        await response.ValidateStatusCodeAsync(this.Logger);
        var content = (await response.Content.ReadAsStringAsync())!;
        return this.Ok(content);
    }

    [HttpGet("/service/info")]
    public async Task<IActionResult> GetServiceInfo()
    {
        var ccfClient = await this.CcfClientManager.GetGovClient();
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
        var ccfClient = await this.CcfClientManager.GetGovClient();
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
        var ccfClient = await this.CcfClientManager.GetGovClient();
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
        var ccfClient = await this.CcfClientManager.GetGovClient();
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
        var ccfClient = await this.CcfClientManager.GetGovClient();
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
        // There is not direct API to retrieve the original bundle that was submitted via set_jsapp.
        var ccfClient = await this.CcfClientManager.GetGovClient();
        JsonObject modules, endpoints;

        using (HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-modules?" +
            $"api-version={this.CcfClientManager.GetGovApiVersion()}"))
        {
            await response.ValidateStatusCodeAsync(this.Logger);
            modules = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        using (HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-app?" +
            $"api-version={this.CcfClientManager.GetGovApiVersion()}&case=original"))
        {
            await response.ValidateStatusCodeAsync(this.Logger);
            endpoints = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }

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

        var endpointsInProposalFormat = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> apiSpec in
            endpoints["endpoints"]!.AsObject().AsEnumerable())
        {
            // Need to transform the endpoints output to the format that the proposal expects.
            // "/contracts": {
            //   "GET": {
            //     "authnPolicies": [
            //       "member_cert",
            //       "user_cert"
            //     ],
            //     "forwardingRequired": "sometimes",
            //     "jsModule": "/endpoints/contracts.js",
            // =>
            // "/contracts": {
            //   "get": {
            //     "authn_policies": [
            //       "member_cert",
            //       "user_cert"
            //     ],
            //     "forwarding_required": "sometimes",
            //     "js_module": "endpoints/contracts.js",=>
            string api = apiSpec.Key;
            if (endpointsInProposalFormat[api] == null)
            {
                endpointsInProposalFormat[api] = new JsonObject();
            }

            foreach (KeyValuePair<string, JsonNode?> verbSpec in
                apiSpec.Value!.AsObject().AsEnumerable())
            {
                string verb = verbSpec.Key!.ToLower();
                var value = new JsonObject();
                foreach (var item3 in verbSpec.Value!.AsObject().AsEnumerable())
                {
                    value[item3.Key] = item3.Value?.DeepClone();
                }

                // Remove leading / ie "js_module": "/foo/bar" => "js_module": "foo/bar"
                value["js_module"] = value["js_module"]!.ToString().TrimStart('/');

                // The /javascript-app API is not returning mode value for PUT/POST. Need to fill it
                // or else proposal submission fails.
                if ((verb == "put" || verb == "post") && value["mode"] == null)
                {
                    value["mode"] = "readwrite";
                }

                endpointsInProposalFormat[api]!.AsObject()[verb] = value;
            }
        }

        await Task.WhenAll(fetchModuleTasks);
        var modulesArray = new JsonArray();
        for (int i = 0; i < moduleNames.Count; i++)
        {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            string content = (await fetchModuleTasks[i])!;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            modulesArray.Add(new JsonObject
            {
                ["name"] = moduleNames[i].TrimStart('/'),
                ["module"] = content
            });
        }

        return this.Ok(new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["endpoints"] = endpointsInProposalFormat
            },
            ["modules"] = modulesArray
        });
    }

    public class ConfigView
    {
        public WorkspaceConfiguration? Config { get; set; } = default!;

        public System.Collections.IDictionary EnvironmentVariables { get; set; } = default!;
    }
}
