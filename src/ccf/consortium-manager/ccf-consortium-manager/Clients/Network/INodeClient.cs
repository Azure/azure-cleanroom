// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgr.Clients.Node.Models;

namespace CcfConsortiumMgr.Clients.Node;

public interface INodeClient
{
    Task<QuotesList> GetNodeQuotes();

    Task<Network> GetNetwork();
}
