// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class InferencingClientBaseController : AgentBaseController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public InferencingClientBaseController(
        ILogger logger,
        IConfiguration configuration,
        ActiveUserChecker activeUserChecker,
        GovernanceClientManager governanceClientManager)
        : base(logger, configuration, activeUserChecker, governanceClientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    protected async Task SetInferencingFrontendAsPodPolicyAdmin()
    {
        var hostData = this.configuration[SettingName.InferencingFrontendSnpHostData];
        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/podpolicies/admin");
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
            $"Setting up delegate podpolicies/admin cleanroom policy " +
            $"{JsonSerializer.Serialize(addPolicy)}.");
        using HttpResponseMessage response = await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    protected async Task SetSecretAccessPolicy(string secretId, InferencingServicePolicy policy)
    {
        var pcr4Values = MergePcrValues(
            policy.Predictor.Pcrs["4"],
            policy.Transformer.Pcrs["4"]);
        var pcr7Values = MergePcrValues(
            policy.Predictor.Pcrs["7"],
            policy.Transformer.Pcrs["7"]);

        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"secrets/{secretId}/cleanroompolicy");
        var addPolicy = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-cvm",
            ["claims"] = new JsonObject
            {
                ["pcr4"] = pcr4Values,
                ["pcr7"] = pcr7Values
            }
        };

        request.Content = JsonContent.Create(addPolicy);

        this.logger.LogInformation(
            $"For secret id '{secretId}' setting access policy " +
            $"{JsonSerializer.Serialize(addPolicy)}.");
        using HttpResponseMessage response = await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    protected async Task SetIdpTokenAccessPolicy(string subject, InferencingServicePolicy policy)
    {
        // TODO (gsinha): Add the agent's host data to the token policy in addition to that of the
        // inferencing service pods so that the agent can get the token from CGS.
        // We need to add support for both mix of caci and cvm policies.
        // var agentHostData = await Attestation.GetCACIHostData();
        var pcr4Values = MergePcrValues(
            policy.Predictor.Pcrs["4"],
            policy.Transformer.Pcrs["4"]);
        var pcr7Values = MergePcrValues(
            policy.Predictor.Pcrs["7"],
            policy.Transformer.Pcrs["7"]);

        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/oauth-federation-subjects/{subject}");
        var addPolicy = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-cvm",
            ["claims"] = new JsonObject
            {
                ["pcr4"] = pcr4Values,
                ["pcr7"] = pcr7Values
            }
        };

        request.Content = JsonContent.Create(addPolicy);

        this.logger.LogInformation(
            $"For IDP token with subject value '{subject}' setting up delegate access policy " +
            $"{JsonSerializer.Serialize(addPolicy)}.");
        using HttpResponseMessage response = await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    protected async Task SetEventsEmissionPolicy(InferencingServicePolicy policy)
    {
        var pcr4Values = MergePcrValues(
            policy.Predictor.Pcrs["4"],
            policy.Transformer.Pcrs["4"]);
        var pcr7Values = MergePcrValues(
            policy.Predictor.Pcrs["7"],
            policy.Transformer.Pcrs["7"]);

        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/events/writer");
        var addPolicy = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-cvm",
            ["claims"] = new JsonObject
            {
                ["pcr4"] = pcr4Values,
                ["pcr7"] = pcr7Values
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

    protected async Task SetEndorsedCertPolicy(InferencingServicePolicy policy)
    {
        var pcr4Values = MergePcrValues(
            policy.Predictor.Pcrs["4"],
            policy.Transformer.Pcrs["4"]);
        var pcr7Values = MergePcrValues(
            policy.Predictor.Pcrs["7"],
            policy.Transformer.Pcrs["7"]);

        var govClient = this.GovernanceClientManager.GetClient();
        using HttpRequestMessage request =
            new(HttpMethod.Post, $"cleanroompolicy/delegates/ca/endorsed-cert");
        var addPolicy = new JsonObject
        {
            ["type"] = "add",
            ["policyType"] = "snp-cvm",
            ["claims"] = new JsonObject
            {
                ["pcr4"] = pcr4Values,
                ["pcr7"] = pcr7Values
            }
        };

        request.Content = JsonContent.Create(addPolicy);

        this.logger.LogInformation(
            $"Setting up delegate ca/endorsed-cert cleanroom policy " +
            $"{JsonSerializer.Serialize(addPolicy)} " +
            $"for inferencing pods.");
        using HttpResponseMessage response = await govClient.SendAsync(request);
        await response.ValidateStatusCodeAsync(this.logger);
    }

    // Merges PCR value lists from predictor and transformer into a single
    // deduplicated JsonArray for governance policy claims.
    private static JsonArray MergePcrValues(
        List<string> predictorValues,
        List<string> transformerValues)
    {
        var seen = new HashSet<string>(predictorValues);
        var merged = new JsonArray();
        foreach (var v in predictorValues)
        {
            merged.Add(v);
        }

        foreach (var v in transformerValues)
        {
            if (!seen.Contains(v))
            {
                merged.Add(v);
            }
        }

        return merged;
    }
}