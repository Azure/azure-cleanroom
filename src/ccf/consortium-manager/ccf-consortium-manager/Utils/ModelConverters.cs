// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CMModels = CcfConsortiumMgr.Models;
using GovModels = CcfConsortiumMgr.Clients.Governance.Models;

namespace CcfConsortiumMgr.Utils;

public static class ModelConverters
{
    public static GovModels.UserIdentity ToUserIdentity(this CMModels.UserIdentity userIdentity)
    {
        return new GovModels.UserIdentity()
        {
            AccountType = userIdentity.AccountType.ToGovernanceAccountType(),
            TenantId = userIdentity.TenantId,
            ObjectId = userIdentity.ObjectId
        };
    }

    public static GovModels.UserIdentity ToUserIdentity(this GovModels.Invitation invitation)
    {
        return new GovModels.UserIdentity()
        {
            InvitationId = invitation.InvitationId,
            AccountType = invitation.AccountType,
            ObjectId = invitation.UserInfo.UserId,
            TenantId = invitation.UserInfo.UserData.TenantId,
            Identifier =
                invitation.Claims?.PreferredUsername?.First() ??
                invitation.Claims?.AppId.First()
        };
    }

    public static GovModels.UserInvitation ToUserInvitation(
        this CMModels.UserInvitation userInvitation)
    {
        return new GovModels.UserInvitation()
        {
            InvitationId = userInvitation.InvitationId,
            TenantId = userInvitation.TenantId,
            IdentityType = userInvitation.IdentityType.ToGovernanceIdentityType(),
            IdentityName = userInvitation.IdentityName,
            AccountType = userInvitation.AccountType.ToGovernanceAccountType()
        };
    }

    private static GovModels.AccountType ToGovernanceAccountType(
        this CMModels.AccountType accountType)
    {
        return accountType switch
        {
            CMModels.AccountType.microsoft => GovModels.AccountType.microsoft,
            _ => throw new NotSupportedException($"Account type: {accountType} is not supported.")
        };
    }

    private static GovModels.IdentityType ToGovernanceIdentityType(
        this CMModels.IdentityType identityType)
    {
        return identityType switch
        {
            CMModels.IdentityType.ServicePrincipal => GovModels.IdentityType.ServicePrincipal,
            CMModels.IdentityType.User => GovModels.IdentityType.User,
            _ => throw new NotSupportedException($"Identity type: {identityType} is not supported.")
        };
    }
}
