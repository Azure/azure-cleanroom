// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ReportsController : ControllerBase
{
    private readonly IConfiguration config;
    private readonly ILogger<ReportsController> logger;

    public ReportsController(
        IConfiguration config,
        ILogger<ReportsController> logger)
    {
        this.config = config;
        this.logger = logger;
    }

    [HttpPost("/generate/rsa")]
    public async Task<IActionResult> GetReportWithKeys()
    {
        var reportAndKey = await Attestation.GenerateRsaKeyPairAndReportAsync();
        return this.Ok(reportAndKey);
    }

    [HttpPost("/generate/ecdsa")]
    public async Task<IActionResult> GetReportWithEcdsaKeys()
    {
        var reportAndKey = await Attestation.GenerateEcdsaKeyPairAndReportAsync();
        return this.Ok(reportAndKey);
    }

    [HttpPost("/generate/maa_request")]
    public async Task<IActionResult> GenerateMaaRequest([FromBody] MaaRequestBody body)
    {
        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri(
                $"http://localhost:{Environment.GetEnvironmentVariable("SKR_PORT") ?? "8284"}")
        };

        using var response = await httpClient.PostAsync("/attest/maa", JsonContent.Create(new
        {
            maa_endpoint = "sharedneu.neu.attest.azure.net",
            runtime_data = body.RuntimeData
        }));
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync();
            throw new Azure.RequestFailedException((int)response.StatusCode, content);
        }

        return this.Ok();
    }

    [HttpPost("/extract/maa_request")]
    public async Task<IActionResult> ExtractMaaRequest()
    {
        var filePath = "/shared/skr.log";
        if (!System.IO.File.Exists(filePath))
        {
            return this.BadRequest(new JsonObject
            {
                ["code"] = "FileNotFound",
                ["message"] = $"Log file not found at {filePath}"
            });
        }

        string? lastMatch = null;
        string pattern = "level=debug msg=MAA Request:";
        await foreach (var line in System.IO.File.ReadLinesAsync("/shared/skr.log"))
        {
            if (line.Contains(pattern))
            {
                int startIndex = line.IndexOf('{');
                if (startIndex >= 0)
                {
                    lastMatch = line.Substring(startIndex);
                }
            }
        }

        if (lastMatch == null)
        {
            return this.BadRequest(new JsonObject
            {
                ["code"] = "MaaRequestNotFound",
                ["message"] = $"Log file does not contain the line '{pattern}'."
            });
        }

        JsonObject? requestJson;
        try
        {
            requestJson = JsonNode.Parse(lastMatch) as JsonObject;
        }
        catch (JsonException ex)
        {
            return this.BadRequest(new JsonObject
            {
                ["code"] = "JsonException",
                ["message"] = ex.Message
            });
        }

        if (requestJson == null)
        {
            return this.BadRequest(new JsonObject
            {
                ["code"] = "RequestJsonNotSet",
                ["message"] = $"JsonNode parsing did not return an object."
            });
        }

        return this.Ok(requestJson);
    }

    public class MaaRequestBody
    {
        public string RuntimeData { get; set; } = default!;
    }
}