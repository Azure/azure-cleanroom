// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CleanRoomProvider;

namespace CcfConsortiumMgr.Workloads.Analytics;

public class AnalyticsWorkload : IWorkload
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;

    public AnalyticsWorkload(ILogger logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    public async Task<(JsonObject, JsonObject)> GenerateDeploymentSpec(
        JsonObject contractDataJson,
        string policyCreationOption)
    {
        string agentPolicyRego;
        AgentChartValues agentChartValues;
        var contractData = JsonSerializer.Deserialize<ContractData>(contractDataJson)!;
        var policyConfiguration = new SecurityPolicyConfiguration()
        {
            PolicyCreationOption = Enum.Parse<SecurityPolicyCreationOption>(policyCreationOption)
        };

        if (policyConfiguration.PolicyCreationOption ==
            SecurityPolicyCreationOption.allowAll)
        {
            var frontendSecurityPolicyDigest = AciConstants.AllowAllPolicyDigest;
            agentChartValues = AgentChartValues.ToAgentChartValues(
                contractData,
                telemetryCollectionEnabled: false,
                Constants.SparkFrontendEndpoint,
                frontendSecurityPolicyDigest);
            agentPolicyRego = AciConstants.AllowAllPolicyRego;
        }
        else
        {
            (var frontendPolicyRego, _) =
                await ImageUtils.DownloadAndExpandSparkFrontendPolicy(
                    policyConfiguration.PolicyCreationOption,
                    telemetryCollectionEnabled: false);
            var frontendSecurityPolicyDigest = ToPolicyDigest(frontendPolicyRego);
            agentChartValues = AgentChartValues.ToAgentChartValues(
                contractData,
                telemetryCollectionEnabled: false,
                Constants.SparkFrontendEndpoint,
                frontendSecurityPolicyDigest);
            (agentPolicyRego, _) =
                await ImageUtils.DownloadAndExpandAnalyticsAgentPolicy(
                    policyConfiguration.PolicyCreationOption,
                    agentChartValues);
        }

        var deploymentTemplate =
            new DeploymentTemplate()
            {
                ChartMetadata = new()
                {
                    Chart = ImageUtils.GetAnalyticsAgentChartPath(),
                    Version = ImageUtils.GetAnalyticsAgentChartVersion(),
                    Release = Constants.AnalyticsAgentReleaseName,
                    Namespace = Constants.AnalyticsAgentNamespace
                },
                Values = agentChartValues
            };
        var governancePolicy =
            new GovernancePolicyOutput()
            {
                Type = "add",
                PolicyType = "snp-caci",
                Claims = new()
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] = ToPolicyDigest(agentPolicyRego)
                }
            };
        return (
            JsonSerializer.SerializeToNode(deploymentTemplate)!.AsObject(),
            JsonSerializer.SerializeToNode(governancePolicy)!.AsObject());
    }

    private static string ToPolicyDigest(string policyRego)
    {
        return BitConverter.ToString(SHA256.HashData(Encoding.UTF8.GetBytes(policyRego)))
        .Replace("-", string.Empty).ToLower();
    }
}
