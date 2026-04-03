// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Clients.RecoveryService.Models;

namespace CcfConsortiumMgr.Clients.RecoveryService;

public interface IRecoveryServiceClient
{
    Task<RecoveryServiceReport> GetRecoveryServiceReport();

    Task<RecoveryServiceMember> GetRecoveryServiceMember(string memberName);
}
