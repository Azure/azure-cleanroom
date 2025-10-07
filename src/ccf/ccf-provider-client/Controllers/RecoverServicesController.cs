// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Azure.Core;
using CcfCommon;
using CcfProviderClient;
using CcfRecoveryProvider;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class RecoverServicesController : CCfClientController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public RecoverServicesController(
        ILogger logger,
        IConfiguration configuration,
        ProvidersRegistry providers)
        : base(logger, configuration, providers)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    [HttpPost("/recoveryServices/{serviceName}/create")]
    public async Task<IActionResult> PutRecoveryService(
        [FromRoute] string serviceName,
        [FromBody] PutRecoveryServiceInput content)
    {
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        CcfRecoveryServiceProvider svcProvider = this.GetRecoveryServiceProvider(content.InfraType);
        CcfRecoveryService svc =
            await svcProvider.CreateService(
                serviceName,
                content.AkvEndpoint,
                content.MaaEndpoint,
                content.ManagedIdentityId,
                content.CcfNetworkJoinPolicy,
                SecurityPolicyConfigInput.Convert(content.SecurityPolicy),
                content.ProviderConfig);
        return this.Ok(svc);

        IActionResult? ValidateCreateInput()
        {
            if (string.IsNullOrEmpty(content.AkvEndpoint))
            {
                return this.BadRequest(new ODataError(
                    code: "InputMissing",
                    message: "akvEndpoint must be specified."));
            }

            if (string.IsNullOrEmpty(content.MaaEndpoint))
            {
                return this.BadRequest(new ODataError(
                    code: "InputMissing",
                    message: "maaEndpoint must be specified."));
            }

            if (content.InfraType == nameof(RsInfraType.caci))
            {
                if (string.IsNullOrEmpty(content.ManagedIdentityId))
                {
                    return this.BadRequest(new ODataError(
                        code: "InputMissing",
                        message: "managedIdentityId must be specified."));
                }

                try
                {
                    new ResourceIdentifier(content.ManagedIdentityId);
                }
                catch (Exception e)
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidManagedIdentityId",
                        message: e.Message));
                }
            }

            var error = this.ValidateNetworkJoinPolicy(content.CcfNetworkJoinPolicy);
            if (error != null)
            {
                return this.BadRequest(error);
            }

            return null;
        }
    }

    [HttpPost("/recoveryServices/{serviceName}/delete")]
    public async Task<IActionResult> DeleteRecoveryService(
        [FromRoute] string serviceName,
        [FromBody] DeleteRecoveryServiceInput content)
    {
        CcfRecoveryServiceProvider svcProvider = this.GetRecoveryServiceProvider(content.InfraType);
        await svcProvider.DeleteService(serviceName, content.ProviderConfig);
        return this.Ok();
    }

    [HttpPost("/recoveryServices/{serviceName}/get")]
    public async Task<IActionResult> GetRecoveryService(
        [FromRoute] string serviceName,
        [FromBody] GetRecoveryServiceInput content)
    {
        CcfRecoveryServiceProvider svcProvider = this.GetRecoveryServiceProvider(content.InfraType);
        CcfRecoveryService? svc = await svcProvider.GetService(serviceName, content.ProviderConfig);
        if (svc != null)
        {
            return this.Ok(svc);
        }

        return this.NotFound(new ODataError(
            code: "ServiceNotFound",
            message: $"No endpoint for service {serviceName} was found."));
    }

    [HttpPost("/recoveryServices/generateSecurityPolicy")]
    public async Task<IActionResult> GenerateSecurityPolicy(
        [FromBody] RsGenerateSecurityPolicyInput content)
    {
        SecurityPolicyCreationOption policyOption =
            CcfUtils.ToOptionOrDefault(content.SecurityPolicyCreationOption);
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        CcfRecoveryServiceProvider svcProvider = this.GetRecoveryServiceProvider(content.InfraType);
        JsonObject result = await svcProvider.GenerateSecurityPolicy(
            content.CcfNetworkJoinPolicy,
            policyOption);
        return this.Ok(result);

        IActionResult? ValidateCreateInput()
        {
            if (policyOption == SecurityPolicyCreationOption.userSupplied)
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidInput",
                    message: $"securityPolicyCreationOption {policyOption} is not applicable."));
            }

            var error = this.ValidateNetworkJoinPolicy(content.CcfNetworkJoinPolicy);
            if (error != null)
            {
                return this.BadRequest(error);
            }

            return null;
        }
    }

    private ODataError? ValidateNetworkJoinPolicy(NetworkJoinPolicy? policy)
    {
        if (policy == null)
        {
            return new ODataError(
                code: "PolicyMissing",
                message: "CCF NetworkJoinPolicy must be supplied.");
        }

        if (policy.Snp == null)
        {
            return new ODataError(
                code: "SnpKeyMissing",
                message: "snp key is missing");
        }

        if (policy.Snp.HostData == null || policy.Snp.HostData.Count == 0)
        {
            return new ODataError(
                code: "HostDataKeyMissing",
                message: "snp.hostData value is missing");
        }

        if (policy.Snp.HostData.Any(x => x.Length != 64))
        {
            return new ODataError(
                code: "InvalidHostData",
                message: "hostData hex string must have 64 characters.");
        }

        return null;
    }
}
