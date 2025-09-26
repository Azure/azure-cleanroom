// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgrProvider;
using CcfProviderClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ConsortiumManagersController : CCfClientController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public ConsortiumManagersController(
        ILogger logger,
        IConfiguration configuration,
        ProvidersRegistry providers)
        : base(logger, configuration, providers)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    [HttpPost("/consortiumManagers/{consortiumManagerName}/create")]
    public async Task<IActionResult> PutConsortiumManager(
        [FromRoute] string consortiumManagerName,
        [FromBody] PutConsortiumManagerInput content)
    {
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        CcfConsortiumManagerProvider provider =
            this.GetConsortiumManagerProvider(content.InfraType);
        CcfConsortiumManager manager =
            await provider.CreateConsortiumManager(
                consortiumManagerName,
                SecurityPolicyConfigInput.Convert(content.SecurityPolicy),
                content.ProviderConfig);
        return this.Ok(manager);

        IActionResult? ValidateCreateInput()
        {
            if (string.IsNullOrEmpty(content.InfraType))
            {
                return this.BadRequest(new ODataError(
                    code: "InputMissing",
                    message: "infraType must be specified."));
            }

            return null;
        }
    }

    [HttpPost("/consortiumManagers/{consortiumManagerName}/get")]
    public async Task<IActionResult> GetConsortiumManager(
        [FromRoute] string consortiumManagerName,
        [FromBody] GetConsortiumManagerInput content)
    {
        CcfConsortiumManagerProvider provider =
            this.GetConsortiumManagerProvider(content.InfraType);
        CcfConsortiumManager? manager =
            await provider.GetConsortiumManager(
                consortiumManagerName,
                content.ProviderConfig);
        if (manager != null)
        {
            return this.Ok(manager);
        }

        return this.NotFound(new ODataError(
            code: "ConsortiumManagerNotFound",
            message: $"No endpoint for consortium manager {consortiumManagerName} was found."));
    }
}
