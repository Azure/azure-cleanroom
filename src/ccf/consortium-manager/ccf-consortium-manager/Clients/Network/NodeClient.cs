// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Clients.Node.Models;

namespace CcfConsortiumMgr.Clients.Node;

internal class NodeClient : INodeClient
{
    private readonly ILogger logger;
    private readonly HttpClient httpClient;

    public NodeClient(ILogger logger, HttpClient httpClient)
    {
        this.httpClient = httpClient;
        this.logger = logger;
    }

    public async Task<QuotesList> GetNodeQuotes()
    {
        return await this.httpClient.SendRequest<QuotesList>(
            this.logger,
            "/node/quotes",
            requestContent: null,
            HttpMethod.Get);
    }

    public async Task<Network> GetNetwork()
    {
        return await this.httpClient.SendRequest<Network>(
            this.logger,
            "/node/network",
            requestContent: null,
            HttpMethod.Get);
    }
}
