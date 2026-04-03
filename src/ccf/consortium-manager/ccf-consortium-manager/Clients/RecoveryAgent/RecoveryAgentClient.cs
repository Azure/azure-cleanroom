// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Clients.RecoveryAgent.Models;

namespace CcfConsortiumMgr.Clients.RecoveryAgent;

internal class RecoveryAgentClient : IRecoveryAgentClient
{
    private readonly ILogger logger;
    private readonly HttpClient httpClient;

    public RecoveryAgentClient(ILogger logger, HttpClient httpClient)
    {
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public async Task<NetworkReport> GetNetworkReport()
    {
        return await this.httpClient.SendRequest<NetworkReport>(
            this.logger,
            "/network/report",
            requestContent: null,
            HttpMethod.Get);
    }
}
