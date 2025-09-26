// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class WorkspacesController : ControllerBase
{
    private readonly CcfClientManager ccfClientManager;

    public WorkspacesController(
        ILogger<WorkspacesController> logger,
        CcfClientManager ccfClientManager)
    {
        this.ccfClientManager = ccfClientManager;
    }

    [HttpGet("/show")]
    public async Task<IActionResult> Show()
    {
        var wsConfig = await this.ccfClientManager.GetWsConfig();
        return this.Ok(new WorkspaceConfigurationModel
        {
            CcrgovEndpoint = wsConfig.CcrgovEndpoint,
            ServiceCert = wsConfig.ServiceCert,
            ServiceCertDiscovery = wsConfig.ServiceCertLocator?.Model,
        });
    }

    public class WorkspaceConfigurationModel
    {
        [JsonPropertyName("ccrgovEndpoint")]
        public string CcrgovEndpoint { get; set; } = default!;

        [JsonPropertyName("serviceCert")]
        public string? ServiceCert { get; set; } = default!;

        [JsonPropertyName("serviceCertDiscovery")]
        public CcfServiceCertDiscoveryModel? ServiceCertDiscovery { get; set; } = default;
    }
}