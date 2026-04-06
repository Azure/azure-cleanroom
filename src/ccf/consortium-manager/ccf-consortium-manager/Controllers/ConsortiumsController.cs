// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CcfConsortiumMgr;

[Authorize]
[ApiController]
public class ConsortiumsController : ControllerBase
{
    private readonly ILogger logger;
    private readonly CcfConsortiumManager ccfConsortiumManager;

    public ConsortiumsController(
        ILogger logger,
        CcfConsortiumManager ccfConsortiumManager)
    {
        this.logger = logger;
        this.ccfConsortiumManager = ccfConsortiumManager;
    }

    [HttpPost("/consortiums/getMemberDetails")]
    public async Task<IActionResult> GetMemberDetails()
    {
        try
        {
            var memberDetails = await this.ccfConsortiumManager.GetConsortiumManagerMember();
            return this.Ok(memberDetails);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in GetMemberDetails.");
            throw;
        }
    }

    [HttpPost("/consortiums/prepareConsortium")]
    public async Task<IActionResult> PrepareConsortium(
        [FromBody] PrepareConsortiumInput prepareConsortiumInput)
    {
        try
        {
            prepareConsortiumInput.Validate();

            await this.ccfConsortiumManager.PrepareConsortium(
                prepareConsortiumInput.CcfEndpoint,
                prepareConsortiumInput.CcfServiceCertPem,
                prepareConsortiumInput.RecoveryAgentEndpoint,
                prepareConsortiumInput.RecoveryServiceEndpoint,
                prepareConsortiumInput.UserIdentity);
            return this.Ok();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in PrepareConsortium.");
            throw;
        }
    }

    [HttpPost("/consortiums/validateConsortium")]
    public async Task<IActionResult> ValidateConsortium(
        [FromBody] ValidateConsortiumInput validateConsortiumInput)
    {
        try
        {
            validateConsortiumInput.Validate();

            string serviceCert =
                await this.ccfConsortiumManager.ValidateConsortium(
                    validateConsortiumInput.CcfEndpoint,
                    validateConsortiumInput.RecoveryAgentEndpoint,
                    validateConsortiumInput.RecoveryServiceEndpoint);

            var report =
                new ConsortiumValidationReport()
                {
                    ServiceCert = serviceCert
                };
            return this.Ok(report);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in ValidateConsortium.");
            throw;
        }
    }

    [HttpPost("/consortiums/recoverConsortium")]
    public async Task<IActionResult> RecoverConsortium(
        [FromBody] RecoverConsortiumInput recoverConsortiumInput)
    {
        try
        {
            recoverConsortiumInput.Validate();

            await this.ccfConsortiumManager.RecoverConsortium(
                recoverConsortiumInput.CcfEndpoint,
                recoverConsortiumInput.CcfServiceCertPem);
            return this.Ok();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in RecoverConsortium.");
            throw;
        }
    }

    [HttpPost("/consortiums/generateWorkloadContract")]
    public async Task<IActionResult> GenerateWorkloadContract(
        [FromBody] GenerateWorkloadContractInput generateWorkloadContractInput)
    {
        try
        {
            generateWorkloadContractInput.Validate();

            await this.ccfConsortiumManager.GenerateWorkloadContract(
                generateWorkloadContractInput.CcfEndpoint,
                generateWorkloadContractInput.CcfServiceCertPem,
                generateWorkloadContractInput.RecoveryAgentEndpoint,
                generateWorkloadContractInput.RecoveryServiceEndpoint,
                generateWorkloadContractInput.CcfProviderConfig,
                generateWorkloadContractInput.WorkloadType,
                generateWorkloadContractInput.ContractId,
                generateWorkloadContractInput.PolicyCreationOption);
            return this.Ok();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in GenerateWorkloadContract.");
            throw;
        }
    }

    [HttpPost("/consortiums/setDeploymentInfo")]
    public async Task<IActionResult> SetDeploymentInfo(
        [FromBody] SetDeploymentInfoInput setDeploymentInfoInput)
    {
        try
        {
            setDeploymentInfoInput.Validate();

            await this.ccfConsortiumManager.SetDeploymentInfo(
                setDeploymentInfoInput.CcfEndpoint,
                setDeploymentInfoInput.CcfServiceCertPem,
                setDeploymentInfoInput.ContractId,
                setDeploymentInfoInput.DeploymentInfo);
            return this.Ok();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in SetDeploymentInfo.");
            throw;
        }
    }
}
