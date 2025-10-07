// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using AttestationClient;
using CcfProvider;
using CcfProviderClient;
using LoadBalancerProvider;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class RecoveryAgentsController : CCfClientController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly RecoveryAgentClientManager agentClientManager;

    public RecoveryAgentsController(
        ILogger logger,
        IConfiguration configuration,
        RecoveryAgentClientManager agentClientManager,
        ProvidersRegistry providers)
        : base(logger, configuration, providers)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.agentClientManager = agentClientManager;
    }

    [HttpPost("/networks/{networkName}/recoveryAgents/get")]
    public async Task<IActionResult> GetRecoveryAgent(
        [FromRoute] string networkName,
        [FromBody] GetNetworkRecoveryAgentInput content)
    {
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider =
            this.GetRecoveryAgentProvider(content.InfraType);
        CcfNetworkRecoveryAgents? agent = await recoveryAgentProvider.GetNetworkRecoveryAgent(
            networkName,
            content.ProviderConfig);
        if (agent != null)
        {
            return this.Ok(agent);
        }

        return this.NotFound(new ODataError(
            code: "RecoveryAgentNotFound",
            message: $"No recovery agent endpoint for {networkName} was found."));
    }

    [HttpPost("/networks/{networkName}/recoveryAgents/report")]
    public async Task<IActionResult> GetRecoveryAgentReport(
        [FromRoute] string networkName,
        [FromBody] GetNetworkRecoveryAgentInput content,
        [FromQuery] bool? skipVerify = false)
    {
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider =
            this.GetRecoveryAgentProvider(content.InfraType);
        CcfNetworkRecoveryAgentsReport agentRreport = await recoveryAgentProvider.GetReport(
            networkName,
            content.ProviderConfig);
        if (!skipVerify.GetValueOrDefault() && agentRreport.Reports != null)
        {
            foreach (var item in agentRreport.Reports)
            {
                var report = item.Report?["report"];
                if (report != null)
                {
                    var attestationReport = JsonSerializer.Deserialize<AttestationReport>(report)!;
                    SnpReport.VerifySnpAttestation(
                        attestationReport.Attestation,
                        attestationReport.PlatformCertificates,
                        attestationReport.UvmEndorsements);
                    item.Verified = true;
                }
            }
        }

        return this.Ok(agentRreport);
    }

    [HttpPost("/networks/{networkName}/recoveryAgents/network/report")]
    public async Task<IActionResult> GetCcfNetworkReport(
        [FromRoute] string networkName,
        [FromBody] GetNetworkRecoveryAgentInput content,
        [FromQuery] bool? skipVerify = false)
    {
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider =
            this.GetRecoveryAgentProvider(content.InfraType);
        CcfNetworkRecoveryAgentsReport agentRreport = await recoveryAgentProvider.GetNetworkReport(
            networkName,
            content.ProviderConfig);
        if (!skipVerify.GetValueOrDefault() && agentRreport.Reports != null)
        {
            foreach (var item in agentRreport.Reports)
            {
                var report = item.Report?["report"];
                if (report != null)
                {
                    var attestationReport = JsonSerializer.Deserialize<AttestationReport>(report)!;
                    SnpReport.VerifySnpAttestation(
                        attestationReport.Attestation,
                        attestationReport.PlatformCertificates,
                        attestationReport.UvmEndorsements);
                    item.Verified = true;
                }
            }
        }

        return this.Ok(agentRreport);
    }

    [HttpPost("/networks/{networkName}/recoveryAgents/recoveryMembers/generate")]
    public async Task<IActionResult> GenerateRecoveryMember(
        [FromRoute] string networkName,
        [FromBody] GenerateRecoveryMemberInput content)
    {
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider =
            this.GetRecoveryAgentProvider(content.InfraType);
        var result = await recoveryAgentProvider.GenerateRecoveryMember(
            networkName,
            content.MemberName,
            content.AgentConfig,
            content.ProviderConfig);
        return this.Ok(result);
    }

    [HttpPost("/networks/{networkName}/recoveryAgents/recoveryMembers/activate")]
    public async Task<IActionResult> ActivateRecoveryMember(
        [FromRoute] string networkName,
        [FromBody] ActivateRecoveryMemberInput content)
    {
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider =
            this.GetRecoveryAgentProvider(content.InfraType);
        var result = await recoveryAgentProvider.ActivateRecoveryMember(
            networkName,
            content.MemberName,
            content.AgentConfig,
            content.ProviderConfig);
        return this.Ok(result);
    }

    [HttpPost("/networks/{networkName}/recoveryAgents/recoveryMembers/submitRecoveryShare")]
    public async Task<IActionResult> SubmitRecoveryShare(
        [FromRoute] string networkName,
        [FromBody] SubmitRecoveryMemberShareInput content)
    {
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider =
            this.GetRecoveryAgentProvider(content.InfraType);
        var result = await recoveryAgentProvider.SubmitRecoveryShare(
            networkName,
            content.MemberName,
            content.AgentConfig,
            content.ProviderConfig);
        return this.Ok(result);
    }

    [HttpPost("/networks/{networkName}/recoveryAgents/network/joinpolicy/set")]
    public async Task<IActionResult> SetNetworkJoinPolicy(
        [FromRoute] string networkName,
        [FromBody] SetNetworkJoinPolicyInput content)
    {
        CcfNetworkRecoveryAgentProvider recoveryAgentProvider =
            this.GetRecoveryAgentProvider(content.InfraType);
        await recoveryAgentProvider.SetNetworkJoinPolicy(
            networkName,
            content.AgentConfig,
            content.ProviderConfig);
        return this.Ok();
    }

    private CcfNetworkRecoveryAgentProvider GetRecoveryAgentProvider(string infraType)
    {
        InfraType type = Enum.Parse<InfraType>(infraType, ignoreCase: true);
        ICcfNodeProvider nodeProvider = this.GetNodeProvider(type);
        ICcfLoadBalancerProvider lbProvider = this.GetLoadBalancerProvider(type);
        var recoveryAgentProvider = new CcfNetworkRecoveryAgentProvider(
            this.logger,
            nodeProvider,
            lbProvider,
            this.agentClientManager);
        return recoveryAgentProvider;
    }
}
