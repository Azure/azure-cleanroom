// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CcfConsortiumMgr;

[Authorize]
[ApiController]
public class UsersController : ControllerBase
{
    private readonly ILogger logger;
    private readonly CcfConsortiumManager ccfConsortiumManager;

    public UsersController(
        ILogger logger,
        CcfConsortiumManager ccfConsortiumManager)
    {
        this.logger = logger;
        this.ccfConsortiumManager = ccfConsortiumManager;
    }

    [HttpPost("/users/addUserInvitation")]
    public async Task<IActionResult> AddUserInvitation(
        [FromBody] AddUserInvitationInput addUserInvitationInput)
    {
        try
        {
            addUserInvitationInput.Validate();

            await this.ccfConsortiumManager.AddUserInvitation(
                addUserInvitationInput.CcfEndpoint,
                addUserInvitationInput.CcfServiceCertPem,
                addUserInvitationInput.UserInvitation);
            return this.Ok();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in AddUserInvitation.");
            throw;
        }
    }

    [HttpPost("/users/activateUserInvitation")]
    public async Task<IActionResult> ActivateUserInvitation(
        [FromBody] ActivateUserInvitationInput activateUserInvitationInput)
    {
        try
        {
            activateUserInvitationInput.Validate();

            await this.ccfConsortiumManager.ActivateUserInvitation(
                activateUserInvitationInput.CcfEndpoint,
                activateUserInvitationInput.CcfServiceCertPem,
                activateUserInvitationInput.InvitationId);
            return this.Ok();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in ActivateUserInvitation.");
            throw;
        }
    }

    [HttpPost("/users/addUserIdentity")]
    public async Task<IActionResult> AddUserIdentity(
        [FromBody] AddUserIdentityInput addUserIdentityInput)
    {
        try
        {
            addUserIdentityInput.Validate();

            await this.ccfConsortiumManager.AddUserIdentity(
                addUserIdentityInput.CcfEndpoint,
                addUserIdentityInput.CcfServiceCertPem,
                addUserIdentityInput.UserIdentity);
            return this.Ok();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Exception hit in AddUserIdentity.");
            throw;
        }
    }
}
