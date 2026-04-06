// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Clients.RecoveryService.Models;

namespace CcfConsortiumMgr.Clients.RecoveryService;

internal class RecoveryServiceClient : IRecoveryServiceClient
{
    private readonly ILogger logger;
    private readonly HttpClient httpClient;

    public RecoveryServiceClient(ILogger logger, HttpClient httpClient)
    {
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public async Task<RecoveryServiceReport> GetRecoveryServiceReport()
    {
        return await this.httpClient.SendRequest<RecoveryServiceReport>(
            this.logger,
            "/report",
            requestContent: null,
            HttpMethod.Get);
    }

    public async Task<RecoveryServiceMember> GetRecoveryServiceMember(string memberName)
    {
        return await this.httpClient.SendRequest<RecoveryServiceMember>(
            this.logger,
            $"/members/{memberName}",
            requestContent: null,
            HttpMethod.Get);
    }
}
