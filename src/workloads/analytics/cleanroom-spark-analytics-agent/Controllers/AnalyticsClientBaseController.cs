// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AttestationClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class AnalyticsClientBaseController : AgentBaseController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public AnalyticsClientBaseController(
        ILogger logger,
        IConfiguration configuration,
        ActiveUserChecker activeUserChecker,
        GovernanceClientManager governanceClientManager)
        : base(logger, configuration, activeUserChecker, governanceClientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    protected async Task<string> CreateSecret(string secretName, string value)
    {
        this.logger.LogInformation($"Creating secret name '{secretName}' in CGS.");
        var govClient = this.GovernanceClientManager.GetClient();
        string secretId;
        using (HttpRequestMessage request = new(HttpMethod.Put, $"secrets/{secretName}"))
        {
            request.Content = JsonContent.Create(
                new JsonObject
                {
                    ["value"] = value
                });

            using HttpResponseMessage response = await govClient.SendAsync(request);
            await response.ValidateStatusCodeAsync(this.logger);
            var responseBody = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            secretId = responseBody["secretId"]!.ToString();
            this.logger.LogInformation($"Secret id '{secretId}' created in CGS.");
            return secretId;
        }
    }

    protected async Task SetSecretAccessPolicy(string secretId, JobPolicy policy)
    {
        var hostData = new JsonArray
        {
            policy.Driver.HostData,
            policy.Executor.HostData,
        };
        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}/cleanroompolicy");
        var addPolicy = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-caci",
            ["claims"] = new JsonObject
            {
                ["x-ms-sevsnpvm-is-debuggable"] = false,
                ["x-ms-sevsnpvm-hostdata"] = hostData
            }
        };

        request.Content = JsonContent.Create(addPolicy);

        this.logger.LogInformation(
            $"For secret id '{secretId}' setting access policy " +
            $"{JsonSerializer.Serialize(addPolicy)}.");
        using HttpResponseMessage response = await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    protected async Task SetIdpTokenAccessPolicy(string subject, JobPolicy policy)
    {
        // Add the agent's host data to the token policy in addition to that of the spark pods
        // so that the agent can get the token from CGS.
        var agentHostData = await Attestation.GetCACIHostData();

        var hostData = new JsonArray
        {
            policy.Driver.HostData,
            policy.Executor.HostData,
            agentHostData
        };
        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/oauth-federation-subjects/{subject}");
        var addPolicy = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-caci",
            ["claims"] = new JsonObject
            {
                ["x-ms-sevsnpvm-is-debuggable"] = false,
                ["x-ms-sevsnpvm-hostdata"] = hostData
            }
        };

        request.Content = JsonContent.Create(addPolicy);

        this.logger.LogInformation(
            $"For IDP token with subject value '{subject}' setting up delegate access policy " +
            $"{JsonSerializer.Serialize(addPolicy)}.");
        using HttpResponseMessage response = await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    protected async Task SetEventsEmissionPolicy(JobPolicy policy)
    {
        var hostData = new JsonArray
        {
            policy.Driver.HostData,
            policy.Executor.HostData,
        };
        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/events/writer");
        var addPolicy = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-caci",
            ["claims"] = new JsonObject
            {
                ["x-ms-sevsnpvm-is-debuggable"] = false,
                ["x-ms-sevsnpvm-hostdata"] = hostData
            }
        };

        request.Content = JsonContent.Create(addPolicy);

        this.logger.LogInformation(
            $"Setting up delegate events/writer cleanroom policy " +
            $"{JsonSerializer.Serialize(addPolicy)} " +
            $"for spark pods.");
        using HttpResponseMessage response = await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }
}