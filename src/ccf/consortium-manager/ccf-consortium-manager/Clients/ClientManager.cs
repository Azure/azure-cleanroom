// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography.X509Certificates;
using CcfConsortiumMgr.Clients.Governance;
using CcfConsortiumMgr.Clients.Node;
using CcfConsortiumMgr.Clients.RecoveryAgent;
using CcfConsortiumMgr.Clients.RecoveryService;
using Controllers;

namespace CcfConsortiumMgr.Clients;

public class ClientManager
{
    private ILogger logger;
    private HttpClientManager httpClientManager;

    public ClientManager(ILogger logger)
    {
        this.logger = logger;
        this.httpClientManager = new(logger);
    }

    public IGovernanceClient GetGovernanceClient(
        string ccfEndpoint,
        string ccfServiceCert,
        string signingCert,
        string signingKey)
    {
        var httpClient = this.GetOrAddClient(
            "ccf-governance",
            ccfEndpoint,
            ccfServiceCert,
            clientCert: X509Certificate2.CreateFromPem(signingCert, signingKey));

        return new GovernanceClient(this.logger, httpClient, signingCert, signingKey);
    }

    public INodeClient GetNodeClient(string ccfEndpoint, string ccfServiceCert)
    {
        var httpClient = this.GetOrAddClient(
            "ccf-network",
            ccfEndpoint,
            ccfServiceCert);

        return new NodeClient(this.logger, httpClient);
    }

    public INodeClient GetInsecureNodeClient(string ccfEndpoint)
    {
        var httpClient = HttpClientManager.NewInsecureClient(
            ccfEndpoint,
            this.logger,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));

        return new NodeClient(this.logger, httpClient);
    }

    public IRecoveryAgentClient GetInsecureRecoveryAgentClient(string agentEndpoint)
    {
        var httpClient = HttpClientManager.NewInsecureClient(
            agentEndpoint,
            this.logger,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));

        return new RecoveryAgentClient(this.logger, httpClient);
    }

    public IRecoveryServiceClient GetInsecureRecoveryServiceClient(string recoveryServiceEndpoint)
    {
        var httpClient = HttpClientManager.NewInsecureClient(
            recoveryServiceEndpoint,
            this.logger,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger));

        return new RecoveryServiceClient(this.logger, httpClient);
    }

    private HttpClient GetOrAddClient(
        string endpointName,
        string endpointBaseAddress,
        string? endpointCert = null,
        bool skipTlsVerify = false,
        X509Certificate2? clientCert = null)
    {
        var client = this.httpClientManager.GetOrAddClient(
            endpointBaseAddress,
            HttpRetries.Policies.DefaultRetryPolicy(this.logger),
            endpointCert,
            endpointName,
            skipTlsVerify,
            clientCert);
        return client;
    }
}
