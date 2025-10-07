// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

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

    [HttpPost("/generate")]
    public async Task<IActionResult> GetReportWithKeys()
    {
        var reportAndKey = await Attestation.GenerateRsaKeyPairAndReportAsync();
        return this.Ok(reportAndKey);
    }
}