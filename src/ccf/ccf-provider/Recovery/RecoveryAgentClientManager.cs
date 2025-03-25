// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Controllers;
using CoseUtils;
using Microsoft.Extensions.Logging;

namespace CcfProvider;

public class RecoveryAgentClientManager
{
    private readonly ILogger logger;
    private SigningConfiguration signingConfig = default!;
    private HttpClientManager httpClientManager;

    public RecoveryAgentClientManager(ILogger logger)
    {
        this.logger = logger;
        this.httpClientManager = new(logger);
    }

    public void SetSigningConfig(SigningConfiguration wsConfig)
    {
        this.signingConfig = wsConfig;
    }

    public CoseSignKey GetCoseSignKey()
    {
        if (this.signingConfig == null)
        {
            throw new Exception("Invoke /configure first to setup signing cert and key details");
        }

        return this.signingConfig.CoseSignKey;
    }

    public Task<HttpClient> GetClient(string agentEndpoint, string serviceCert)
    {
        var client = this.httpClientManager.GetOrAddClient(
            agentEndpoint,
            serviceCert,
            "recovery-agent",
            retryPolicy: HttpRetries.Policies.GetDefaultRetryPolicy(this.logger));
        return Task.FromResult(client);
    }

    private class AgentClient
    {
        public HttpClient HttpClient { get; set; } = default!;
    }
}
