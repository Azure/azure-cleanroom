// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using static Controllers.JsAppBundle;

namespace Controllers;

public static class HttpClientCcfExtensions
{
    public static async Task<string> GetConstitution(
        this HttpClient ccfClient,
        ILogger logger,
        string govApiVersion = "2024-07-01")
    {
        using HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/constitution?" +
            $"api-version={govApiVersion}");
        await response.ValidateStatusCodeAsync(logger);
        var content = (await response.Content.ReadAsStringAsync())!;
        return content;
    }

    public static async Task<JsAppBundle> GetJSAppBundle(
        this HttpClient ccfClient,
        ILogger logger,
        string govApiVersion = "2024-07-01")
    {
        // There is not direct API to retrieve the original bundle that was submitted via set_jsapp.
        JsonObject modules, endpoints;

        using (HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-modules?" +
            $"api-version={govApiVersion}"))
        {
            await response.ValidateStatusCodeAsync(logger);
            modules = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        }

        using (HttpResponseMessage response = await ccfClient.GetAsync(
            $"gov/service/javascript-app?" +
            $"api-version={govApiVersion}&case=original"))
        {
            await response.ValidateStatusCodeAsync(logger);
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
            $"api-version={govApiVersion}");
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
        var modulesArray = new List<ModuleItem>();
        for (int i = 0; i < moduleNames.Count; i++)
        {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
            string content = (await fetchModuleTasks[i])!;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            modulesArray.Add(new ModuleItem
            {
                Name = moduleNames[i].TrimStart('/'),
                Module = content
            });
        }

        return new JsAppBundle
        {
            Metadata = new MetadataItem
            {
                Endpoints = endpointsInProposalFormat
            },
            Modules = modulesArray
        };
    }
}

public class JsAppBundle
{
    [JsonPropertyName("metadata")]
    public MetadataItem Metadata { get; set; } = default!;

    [JsonPropertyName("modules")]
    public List<ModuleItem> Modules { get; set; } = default!;

    public class MetadataItem
    {
        [JsonPropertyName("endpoints")]
        public JsonObject Endpoints { get; set; } = default!;
    }

    public class ModuleItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = default!;

        [JsonPropertyName("module")]
        public string Module { get; set; } = default!;
    }
}