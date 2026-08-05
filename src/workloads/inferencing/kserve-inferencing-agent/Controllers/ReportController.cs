// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ReportController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public ReportController(ILogger logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    [HttpGet("/report")]
    public async Task<IActionResult> GetAgentReport()
    {
        var serviceCertLocation = this.configuration[SettingName.ServiceCertLocation]
            ?? "/app/service/service-cert.pem";
        if (!Path.Exists(serviceCertLocation))
        {
            return this.StatusCode(
                (int)HttpStatusCode.NotFound,
                new
                {
                    error = new
                    {
                        code = "AgentServiceCertNotFound",
                        message = "Could not locate the service certificate " +
                                  "for the inferencing agent.",
                    },
                });
        }

        var serviceCert = await System.IO.File.ReadAllTextAsync(serviceCertLocation);

        string platform;
        SnpCACIAttestationReport? report = null;
        if (Attestation.IsSnpCACI())
        {
            platform = "snp";
            var bytes = Encoding.UTF8.GetBytes(serviceCert);
            report = (await Attestation.GetCACIReportAsync(bytes)).SnpCaci;
        }
        else
        {
            platform = "virtual";
        }

        string hostData = await Attestation.GetCACIHostData();
        return this.Ok(new AgentReport
        {
            Platform = platform,
            Report = report,
            ServiceCert = serviceCert,
            HostData = hostData,
        });
    }
}
