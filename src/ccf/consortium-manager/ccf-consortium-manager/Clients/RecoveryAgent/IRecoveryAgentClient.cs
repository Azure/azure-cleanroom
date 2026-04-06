// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Clients.RecoveryAgent.Models;

namespace CcfConsortiumMgr.Clients.RecoveryAgent;

public interface IRecoveryAgentClient
{
    Task<NetworkReport> GetNetworkReport();
}
