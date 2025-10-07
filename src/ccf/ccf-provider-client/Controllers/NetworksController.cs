// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using CcfCommon;
using CcfProvider;
using CcfProviderClient;
using LoadBalancerProvider;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class NetworksController : CCfClientController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly CcfClientManager ccfClientManager;
    private readonly RecoveryAgentClientManager agentClientManager;
    private readonly BackgroundTaskQueue queue;
    private readonly IOperationStore operationStore;

    public NetworksController(
        ILogger logger,
        IConfiguration configuration,
        CcfClientManager ccfClientManager,
        RecoveryAgentClientManager agentClientManager,
        ProvidersRegistry providers,
        BackgroundTaskQueue queue,
        IOperationStore operationStore)
        : base(logger, configuration, providers)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.ccfClientManager = ccfClientManager;
        this.agentClientManager = agentClientManager;
        this.queue = queue;
        this.operationStore = operationStore;
    }

    [HttpGet("/operations/{operationId}")]
    public IActionResult GetOperationStatus([FromRoute] string operationId)
    {
        return this.operationStore.GetOperationStatus(operationId, this.HttpContext);
    }

    [HttpPost("/networks/{networkName}/create")]
    public async Task<IActionResult> PutNetwork(
        [FromRoute] string networkName,
        [FromBody] PutNetworkInput content,
        [FromQuery] bool async = false)
    {
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        var initialMembers = content.Members.ConvertAll(x => new InitialMember
        {
            EncryptionPublicKey = x.EncryptionPublicKey,
            Certificate = x.Certificate,
            MemberData = x.MemberData
        });

        if (async)
        {
            await this.queue.PerformAsync(this.operationStore, this.HttpContext, CreateNetwork);
            return this.Accepted();
        }
        else
        {
            CcfNetwork network = await CreateNetwork(NoOpProgressReporter);
            return this.Ok(network);
        }

        IActionResult? ValidateCreateInput()
        {
            if (!string.IsNullOrEmpty(content.NodeLogLevel))
            {
                List<string> allowedValues =
                [
                    "Trace", "Debug", "Info", "Fail", "Fatal"
                ];

                if (!allowedValues.Contains(content.NodeLogLevel))
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidNodeLogLevel",
                        message: $"Value should be one of: {string.Join(",", allowedValues)}"));
                }
            }

            if (content.Members.Count == 0)
            {
                return this.BadRequest(new ODataError(
                    code: "MembersMissing",
                    message: "At least one member is required."));
            }

            foreach (var m in content.Members)
            {
                try
                {
                    using var c = X509Certificate2.CreateFromPem(m.Certificate);
                }
                catch (Exception e)
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidCertificate",
                        message: e.Message));
                }

                if (!string.IsNullOrEmpty(m.EncryptionPublicKey))
                {
                    try
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportFromPem(m.EncryptionPublicKey);
                    }
                    catch (Exception e)
                    {
                        return this.BadRequest(new ODataError(
                            code: "InvalidEncryptionPublicKey",
                            message: e.Message));
                    }
                }
            }

            return null;
        }

        async Task<CcfNetwork> CreateNetwork(IProgress<string> progressReporter)
        {
            return await ccfNetworkProvider.CreateNetwork(
                networkName,
                content.NodeCount,
                initialMembers,
                content.NodeLogLevel,
                SecurityPolicyConfigInput.Convert(content.SecurityPolicy),
                content.ProviderConfig,
                progressReporter);
        }
    }

    [HttpPost("/networks/{networkName}/delete")]
    public async Task<IActionResult> DeleteNetwork(
        [FromRoute] string networkName,
        [FromBody] DeleteNetworkInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        if (!Enum.TryParse<DeleteOption>(content.DeleteOption, out var deleteOption))
        {
            deleteOption = DeleteOption.DeleteStorage;
        }

        await ccfNetworkProvider.DeleteNetwork(
            networkName,
            deleteOption,
            content.ProviderConfig);
        return this.Ok();
    }

    [HttpPost("/networks/{networkName}/get")]
    public async Task<IActionResult> GetNetwork(
        [FromRoute] string networkName,
        [FromBody] GetNetworkInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        CcfNetwork? network = await ccfNetworkProvider.GetNetwork(
            networkName,
            content.ProviderConfig);
        if (network != null)
        {
            return this.Ok(network);
        }

        return this.NotFound(new ODataError(
            code: "NetworkNotFound",
            message: $"No endpoint for network {networkName} was found."));
    }

    [HttpPost("/networks/{networkName}/health")]
    public async Task<IActionResult> GetNetworkHealth(
        [FromRoute] string networkName,
        [FromBody] GetNetworkInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        CcfNetworkHealth health = await ccfNetworkProvider.GetNetworkHealth(
            networkName,
            content.ProviderConfig);
        return this.Ok(health);
    }

    [HttpPost("/networks/{networkName}/update")]
    public async Task<IActionResult> UpdateNetwork(
        [FromRoute] string networkName,
        [FromBody] UpdateNetworkInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);

        CcfNetwork network = await
            ccfNetworkProvider.UpdateNetwork(
                networkName,
                content.NodeCount,
                content.NodeLogLevel,
                SecurityPolicyConfigInput.Convert(content.SecurityPolicy),
                content.ProviderConfig);

        return this.Ok(network);
    }

    [HttpPost("/networks/{networkName}/recoverPublicNetwork")]
    public async Task<IActionResult> RecoverPublicNetwork(
        [FromRoute] string networkName,
        [FromBody] RecoverPublicNetworkInput content)
    {
        var error = ValidateRecoverInput();
        if (error != null)
        {
            return error;
        }

        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        var targetNetworkName = string.IsNullOrEmpty(content.TargetNetworkName) ?
            networkName : content.TargetNetworkName;
        IProgress<string> noOpProgressReporter = new Progress<string>(m => { });
        CcfNetwork network = await
            ccfNetworkProvider.RecoverPublicNetwork(
                targetNetworkName,
                networkName,
                content.NodeCount,
                content.NodeLogLevel,
                SecurityPolicyConfigInput.Convert(content.SecurityPolicy),
                content.PreviousServiceCertificate,
                content.ProviderConfig,
                noOpProgressReporter);

        return this.Ok(network);

        IActionResult? ValidateRecoverInput()
        {
            if (!string.IsNullOrEmpty(content.NodeLogLevel))
            {
                List<string> allowedValues =
                [
                    "Trace", "Debug", "Info", "Fail", "Fatal"
                ];

                if (!allowedValues.Contains(content.NodeLogLevel))
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidNodeLogLevel",
                        message: $"Value should be one of: {string.Join(",", allowedValues)}"));
                }
            }

            try
            {
                using var c = X509Certificate2.CreateFromPem(content.PreviousServiceCertificate);
            }
            catch (Exception e)
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidCertificate",
                    message: e.Message));
            }

            return null;
        }
    }

    [HttpPost("/networks/{networkName}/submitRecoveryShare")]
    public async Task<IActionResult> SubmitRecoveryShare(
        [FromRoute] string networkName,
        [FromBody] SubmitRecoveryShareInput content)
    {
        var error = ValidateSubmitRecoveryShareInput();
        if (error != null)
        {
            return error;
        }

        var signingConfig = await this.ccfClientManager.GetSigningConfig();
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        using RSA rsaEncKey = !string.IsNullOrEmpty(content.EncryptionPrivateKey) ?
            Utils.ToRSAKey(content.EncryptionPrivateKey) :
            await Utils.ToRSAKey(new Uri(content.EncryptionKeyId!));

        JsonObject result = await ccfNetworkProvider.SubmitRecoveryShare(
            networkName,
            signingConfig.CoseSignKey,
            rsaEncKey,
            content.ProviderConfig);

        return this.Ok(result);

        IActionResult? ValidateSubmitRecoveryShareInput()
        {
            if (string.IsNullOrEmpty(content.EncryptionPrivateKey) &&
                string.IsNullOrEmpty(content.EncryptionKeyId))
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidEncryptionKey",
                    message: "Either encryptionPrivateKey or encryptionKeyId must be specified."));
            }

            if (!string.IsNullOrEmpty(content.EncryptionPrivateKey) &&
                !string.IsNullOrEmpty(content.EncryptionKeyId))
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidEncryptionKey",
                    message: "Only one of encryptionPrivateKey or encryptionKeyId must be specified."));
            }

            if (!string.IsNullOrEmpty(content.EncryptionPrivateKey))
            {
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(content.EncryptionPrivateKey);
                }
                catch (Exception e)
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidEncryptionPrivateKey",
                        message: e.Message));
                }
            }
            else
            {
                try
                {
                    new Uri(content.EncryptionKeyId!);
                }
                catch (Exception e)
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidEncryptionKeyId",
                        message: e.Message));
                }
            }

            return null;
        }
    }

    [HttpPost("/networks/{networkName}/recover")]
    public async Task<IActionResult> RecoverNetwork(
        [FromRoute] string networkName,
        [FromBody] RecoverNetworkInput content,
        [FromQuery] bool async = false)
    {
        var error = await ValidateRecoverInput();
        if (error != null)
        {
            return error;
        }

        if (async)
        {
            await this.queue.PerformAsync(this.operationStore, this.HttpContext, RecoverNetwork);
            return this.Accepted();
        }
        else
        {
            CcfNetwork network = await RecoverNetwork(NoOpProgressReporter);
            return this.Ok(network);
        }

        async Task<CcfNetwork> RecoverNetwork(IProgress<string> progressReporter)
        {
            CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
            CcfNetwork network;
            if (content.OperatorRecovery != null)
            {
                using RSA rsaEncKey =
                    !string.IsNullOrEmpty(content.OperatorRecovery.EncryptionPrivateKey) ?
                    Utils.ToRSAKey(content.OperatorRecovery.EncryptionPrivateKey) :
                    await Utils.ToRSAKey(new Uri(content.OperatorRecovery.EncryptionKeyId!));
                network = await ccfNetworkProvider.RecoverNetwork(
                    networkName,
                    content.NodeCount ?? 1,
                    content.NodeLogLevel,
                    SecurityPolicyConfigInput.Convert(content.SecurityPolicy),
                    content.PreviousServiceCertificate,
                    rsaEncKey,
                    content.ProviderConfig,
                    progressReporter);
            }
            else
            {
                var svcName = content.ConfidentialRecovery!.RecoveryServiceName;
                var svcProvider = this.GetRecoveryServiceProvider(content.InfraType);
                var recoveryService = await svcProvider.GetService(
                    svcName,
                    content.ProviderConfig);

                network = await ccfNetworkProvider.RecoverNetwork(
                    networkName,
                    content.NodeCount ?? 1,
                    content.NodeLogLevel,
                    SecurityPolicyConfigInput.Convert(content.SecurityPolicy),
                    content.PreviousServiceCertificate,
                    this.GetRecoveryAgentProvider(content.InfraType),
                    content.ConfidentialRecovery!.MemberName,
                    recoveryService!,
                    content.ProviderConfig,
                    progressReporter);
            }

            return network;
        }

        async Task<IActionResult?> ValidateRecoverInput()
        {
            if (!string.IsNullOrEmpty(content.NodeLogLevel))
            {
                List<string> allowedValues =
                [
                    "Trace", "Debug", "Info", "Fail", "Fatal"
                ];

                if (!allowedValues.Contains(content.NodeLogLevel))
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidNodeLogLevel",
                        message: $"Value should be one of: {string.Join(",", allowedValues)}"));
                }
            }

            if (content.OperatorRecovery != null && content.ConfidentialRecovery != null)
            {
                return this.BadRequest(new ODataError(
                    code: "ConflictingInput",
                    message: "Both operatorRecovery and confidentialRecovery cannot be specified" +
                    " together."));
            }

            if (content.OperatorRecovery == null && content.ConfidentialRecovery == null)
            {
                return this.BadRequest(new ODataError(
                    code: "InputMissing",
                    message: "Either operatorRecovery or confidentialRecovery must be specified."));
            }

            try
            {
                using var c = X509Certificate2.CreateFromPem(content.PreviousServiceCertificate);
            }
            catch (Exception e)
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidCertificate",
                    message: e.Message));
            }

            if (content.OperatorRecovery != null)
            {
                var opRec = content.OperatorRecovery;
                if (string.IsNullOrEmpty(opRec.EncryptionPrivateKey) &&
                    string.IsNullOrEmpty(opRec.EncryptionKeyId))
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidEncryptionKey",
                        message: "Either encryptionPrivateKey or encryptionKeyId must be specified."));
                }

                if (!string.IsNullOrEmpty(opRec.EncryptionPrivateKey) &&
                    !string.IsNullOrEmpty(opRec.EncryptionKeyId))
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidEncryptionKey",
                        message: "Only one of encryptionPrivateKey or encryptionKeyId must be specified."));
                }

                if (!string.IsNullOrEmpty(opRec.EncryptionPrivateKey))
                {
                    try
                    {
                        using var rsa = RSA.Create();
                        rsa.ImportFromPem(opRec.EncryptionPrivateKey);
                    }
                    catch (Exception e)
                    {
                        return this.BadRequest(new ODataError(
                            code: "InvalidEncryptionPrivateKey",
                            message: e.Message));
                    }
                }
                else
                {
                    try
                    {
                        new Uri(opRec.EncryptionKeyId!);
                    }
                    catch (Exception e)
                    {
                        return this.BadRequest(new ODataError(
                            code: "InvalidEncryptionKeyId",
                            message: e.Message));
                    }
                }
            }

            if (content.ConfidentialRecovery != null)
            {
                if (string.IsNullOrEmpty(content.ConfidentialRecovery.MemberName))
                {
                    return this.BadRequest(new ODataError(
                        code: "InputMissing",
                        message: "confidentialRecovery.memberName must be specified."));
                }

                var svcName = content.ConfidentialRecovery!.RecoveryServiceName;
                var svcProvider = this.GetRecoveryServiceProvider(content.InfraType);
                var recoveryService = await svcProvider.GetService(
                    svcName,
                    content.ProviderConfig);
                if (recoveryService == null)
                {
                    return this.NotFound(new ODataError(
                        code: "ServiceNotFound",
                        message: $"No endpoint for service {svcName} was found."));
                }
            }

            return null;
        }
    }

    [HttpPost("/networks/{networkName}/configureConfidentialRecovery")]
    public async Task<IActionResult> ConfigureConfidentialRecovery(
        [FromRoute] string networkName,
        [FromBody] ConfigureConfidentialRecoveryInput content)
    {
        var svcProvider = this.GetRecoveryServiceProvider(content.InfraType);
        var recoveryService = await svcProvider.GetService(
            content.RecoveryServiceName,
            content.ProviderConfig);
        if (recoveryService == null)
        {
            return this.NotFound(new ODataError(
                code: "ServiceNotFound",
                message: $"No endpoint for service {content.RecoveryServiceName} was found."));
        }

        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        await ccfNetworkProvider.ConfigureConfidentialRecovery(
            networkName,
            content.RecoveryMemberName,
            recoveryService,
            this.GetRecoveryAgentProvider(content.InfraType),
            content.ProviderConfig);

        return this.Ok();
    }

    [HttpPost("/networks/{networkName}/snapshots/trigger")]
    public async Task<IActionResult> TriggerSnapshot(
        [FromRoute] string networkName,
        [FromBody] TriggerSnapshotInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result =
            await ccfNetworkProvider.TriggerSnapshot(networkName, content.ProviderConfig);
        return this.Ok(result);
    }

    [HttpPost("/networks/{networkName}/transitionToOpen")]
    public async Task<IActionResult> TransitionToOpen(
        [FromRoute] string networkName,
        [FromBody] TransitionToOpenInput content)
    {
        var error = ValidateTransitionInput();
        if (error != null)
        {
            return error;
        }

        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.TransitionToOpen(
            networkName,
            content.PreviousServiceCertificate,
            content.ProviderConfig);
        return this.Ok(result);

        IActionResult? ValidateTransitionInput()
        {
            if (!string.IsNullOrEmpty(content.PreviousServiceCertificate))
            {
                try
                {
                    using var c = X509Certificate2.CreateFromPem(content.PreviousServiceCertificate);
                }
                catch (Exception e)
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidCertificate",
                        message: e.Message));
                }
            }

            return null;
        }
    }

    [HttpPost("/networks/{networkName}/report")]
    public async Task<IActionResult> GetReport(
        [FromRoute] string networkName,
        [FromBody] GetQuotesInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.GetReport(
            networkName,
            content.ProviderConfig);
        return this.Ok(result);
    }

    [HttpPost("/networks/{networkName}/setRecoveryThreshold")]
    public async Task<IActionResult> SetRecoveryThreshold(
        [FromRoute] string networkName,
        [FromBody] SetRecoveryThresholdInput content)
    {
        var error = ValidateThresholdInput();
        if (error != null)
        {
            return error;
        }

        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.SetRecoveryThreshold(
            networkName,
            content.RecoveryThreshold,
            content.ProviderConfig);
        return this.Ok(result);

        IActionResult? ValidateThresholdInput()
        {
            if (content.RecoveryThreshold < 1)
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidRecoveryThreshold",
                    message: "Value should be between 1 and the number of active recovery members."));
            }

            return null;
        }
    }

    [HttpPost("/networks/{networkName}/addSnpHostData")]
    public async Task<IActionResult> AddSnpHostData(
        [FromRoute] string networkName,
        [FromBody] AddSnpHostDataInput content)
    {
        var error = ValidateAddHostDataInput();
        if (error != null)
        {
            return error;
        }

        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.AddSnpHostData(
            networkName,
            content.HostData,
            content.SecurityPolicy,
            content.ProviderConfig);
        return this.Ok(result);

        IActionResult? ValidateAddHostDataInput()
        {
            if (!string.IsNullOrEmpty(content.SecurityPolicy))
            {
                var hashBytes = SHA256.HashData(Convert.FromBase64String(content.SecurityPolicy));
                var securityPolicyDigest = BitConverter.ToString(hashBytes)
                    .Replace("-", string.Empty).ToLower();
                if (securityPolicyDigest != content.HostData)
                {
                    return this.BadRequest(new ODataError(
                    code: "SecurityPolicyMismatch",
                    message: $"The hash of the security policy {securityPolicyDigest} does " +
                    $"not match digest {content.HostData}"));
                }
            }

            return null;
        }
    }

    [HttpPost("/networks/{networkName}/removeSnpHostData")]
    public async Task<IActionResult> RemoveSnpHostData(
        [FromRoute] string networkName,
        [FromBody] RemoveSnpHostDataInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.RemoveSnpHostData(
            networkName,
            content.HostData,
            content.ProviderConfig);
        return this.Ok(result);
    }

    [HttpPost("/networks/{networkName}/getJoinPolicy")]
    public async Task<IActionResult> GetJoinPolicy(
        [FromRoute] string networkName,
        [FromBody] GetJoinPolicyInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.GetJoinPolicy(
            networkName,
            content.ProviderConfig);
        return this.Ok(result);
    }

    [HttpPost("/networks/generateSecurityPolicy")]
    public async Task<IActionResult> GenerateSecurityPolicy(
        [FromBody] GenerateSecurityPolicyInput content)
    {
        SecurityPolicyCreationOption policyOption =
            CcfUtils.ToOptionOrDefault(content.SecurityPolicyCreationOption);
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.GenerateSecurityPolicy(policyOption);
        return this.Ok(result);

        IActionResult? ValidateCreateInput()
        {
            if (policyOption == SecurityPolicyCreationOption.userSupplied)
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidInput",
                    message: $"securityPolicyCreationOption {policyOption} is not applicable."));
            }

            return null;
        }
    }

    [HttpPost("/networks/generateJoinPolicy")]
    public async Task<IActionResult> GenerateJoinPolicy(
        [FromBody] GenerateJoinPolicyInput content)
    {
        SecurityPolicyCreationOption policyOption =
            CcfUtils.ToOptionOrDefault(content.SecurityPolicyCreationOption);
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.GenerateJoinPolicy(policyOption);
        return this.Ok(result);
    }

    [HttpPost("/networks/{networkName}/generateJoinPolicy")]
    public async Task<IActionResult> GenerateJoinPolicy(
        [FromRoute] string networkName,
        [FromBody] GetJoinPolicyInput content)
    {
        CcfNetworkProvider ccfNetworkProvider = this.GetNetworkProvider(content.InfraType);
        JsonObject result = await ccfNetworkProvider.GenerateJoinPolicy(
            networkName,
            content.ProviderConfig);
        return this.Ok(result);
    }

    private CcfNetworkProvider GetNetworkProvider(string infraType)
    {
        InfraType type = Enum.Parse<InfraType>(infraType, ignoreCase: true);
        ICcfNodeProvider nodeProvider = this.GetNodeProvider(type);
        ICcfLoadBalancerProvider lbProvider = this.GetLoadBalancerProvider(type);
        var ccfNetworkProvider = new CcfNetworkProvider(
            this.logger,
            nodeProvider,
            lbProvider,
            this.ccfClientManager);
        return ccfNetworkProvider;
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
