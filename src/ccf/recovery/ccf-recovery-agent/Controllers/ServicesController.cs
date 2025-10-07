// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using CcfCommon;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ServicesController : BaseController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly ClientManager clientManager;

    public ServicesController(
        ILogger logger,
        IConfiguration configuration,
        ClientManager clientManager)
        : base(logger, configuration, clientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.clientManager = clientManager;
    }

    [HttpGet("/ready")]
    public IActionResult Ready()
    {
        return this.Ok(new JsonObject
        {
            ["status"] = "up"
        });
    }

    [HttpGet("/report")]
    public async Task<IActionResult> GetAgentReport()
    {
        // This API currently requires no attestation report input. Any client can query for
        // this information.
        AgentReport report = await GetReport();
        return this.Ok(report);

        async Task<AgentReport> GetReport()
        {
            var serviceCertLocation =
                this.configuration[SettingName.ServiceCertLocation] ??
                MountPaths.RecoveryAgentServiceCertPemFile;
            if (!Path.Exists(serviceCertLocation))
            {
                throw new ApiException(
                    HttpStatusCode.NotFound,
                    "AgentServiceCertNotFound",
                    "Could not locate the service certificate for the recovery agent.");
            }

            var serviceCert = await System.IO.File.ReadAllTextAsync(serviceCertLocation);

            string platform;
            AttestationReport? report = null;
            if (Attestation.IsSevSnp())
            {
                platform = "snp";
                var bytes = Encoding.UTF8.GetBytes(serviceCert);
                var hash = SHA256.HashData(bytes);
                report = await Attestation.GetReportAsync(hash);
            }
            else
            {
                platform = "virtual";
            }

            return new AgentReport
            {
                Platform = platform,
                Report = report,
                ServiceCert = serviceCert,
            };
        }
    }

    [HttpGet("/network/report")]
    public async Task<IActionResult> GetNetworkReport()
    {
        // This API currently requires no attestation report input. Any client can query for
        // this information.
        ServiceCertReport report = await GetNetworkReport();
        return this.Ok(report);

        async Task<ServiceCertReport> GetNetworkReport()
        {
            string govApiVersion = "2024-07-01";
            var ccfClient = await this.clientManager.GetCcfClient();
            var response = (await ccfClient.GetFromJsonAsync<JsonObject>("node/network"))!;
            var serviceCert = response["service_certificate"]!.ToString();

            var constitution = await ccfClient.GetConstitution(this.logger, govApiVersion);
            var bundle = await ccfClient.GetJSAppBundle(this.logger, govApiVersion);
            var canonicalBundle = ToCanonicalBundle(bundle);

            var cd = BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(constitution)))
                .Replace("-", string.Empty).ToLower();
            var jd = BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalBundle)))
                .Replace("-", string.Empty).ToLower();
            var reportDataPayload = JsonSerializer.Serialize(
                new CcfServiceCertReportDataContent
                {
                    ServiceCert = serviceCert,
                    ConstitutionDigest = $"sha256:{cd}",
                    JsAppBundleDigest = $"sha256:{jd}",
                });
            var reportDataPayloadBytes = Encoding.UTF8.GetBytes(reportDataPayload);
            string platform;
            AttestationReport? report = null;
            if (Attestation.IsSevSnp())
            {
                platform = "snp";
                var hash = SHA256.HashData(reportDataPayloadBytes);
                report = await Attestation.GetReportAsync(hash);
            }
            else
            {
                platform = "virtual";
            }

            return new ServiceCertReport
            {
                Platform = platform,
                Report = report,
                ReportDataPayload = Convert.ToBase64String(reportDataPayloadBytes),
            };
        }
    }

    // Logic in this method matches the
    // 'json.dumps(bundle, indent=2, sort_keys=True, ensure_ascii=False)' output
    // that other clients (like get_current_jsapp_bundle in az cleanroom cli) uses to calculate
    // the bundle digest value. Format of the canonical bundle needs to be identical across
    // clients or else there will be a difference in the computed hash value of the bundle leading
    // to misconfigurations where expected bundle digest values don't match.
    private static string ToCanonicalBundle(JsAppBundle bundle)
    {
        // No native way to do the python equivalent of:
        // json.dumps(bundle, indent=2, sort_keys=True, ensure_ascii=False)
        // hence the logic below to sort keys and indent the output json by 2.
        string inputJson = JsonSerializer.Serialize(bundle);
        using var doc = JsonDocument.Parse(inputJson);
        var sorted = SortJsonElement(doc.RootElement);

        var options = new JsonWriterOptions
        {
            // We'll manually adjust the indent spacing.
            Indented = true,

            // Ensure that the output is not escaped, similar to ensure_ascii=False in Python.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, options))
        {
            WriteSortedElement(writer, sorted, indentSize: 2);
        }

        string output = Encoding.UTF8.GetString(stream.ToArray());
        return output;
    }

    private static JsonElement SortJsonElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        WriteSortedElement(writer, element, indentSize: 0);
        writer.Flush();

        stream.Position = 0;
        using var doc = JsonDocument.Parse(stream);
        return doc.RootElement.Clone();
    }

    private static void WriteSortedElement(
        Utf8JsonWriter writer,
        JsonElement element,
        int indentSize,
        int currentIndent = 0)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSortedElement(
                        writer,
                        property.Value,
                        indentSize,
                        currentIndent + indentSize);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSortedElement(writer, item, indentSize, currentIndent + indentSize);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}