// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CleanRoomProvider;
using CleanRoomProviderClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class ClustersController : ClusterClientController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly BackgroundTaskQueue queue;
    private readonly IOperationStore operationStore;

    public ClustersController(
        ILogger logger,
        IConfiguration configuration,
        ProvidersRegistry providers,
        BackgroundTaskQueue queue,
        IOperationStore operationStore)
        : base(logger, configuration, providers)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.queue = queue;
        this.operationStore = operationStore;
    }

    [HttpGet("/operations/{operationId}")]
    public IActionResult GetOperationStatus([FromRoute] string operationId)
    {
        return this.operationStore.GetOperationStatus(operationId, this.HttpContext);
    }

    [HttpPost("/clusters/{clusterName}/create")]
    public async Task<IActionResult> PutCleanRoomCluster(
        [FromRoute] string clusterName,
        [FromBody] PutClusterInput content,
        [FromQuery] bool async = false)
    {
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);
        var clusterInput = new CleanRoomClusterInput
        {
            ObservabilityProfile = content.ObservabilityProfile,
            MonitoringProfile = content.MonitoringProfile,
            AnalyticsWorkloadProfile = content.AnalyticsWorkloadProfile,
            InferencingWorkloadProfile = content.InferencingWorkloadProfile,
            FlexNodeProfile = content.FlexNodeProfile,
            AadProfile = content.AadProfile
        };
        var pError =
            provider.CreateClusterValidate(clusterName, clusterInput, content.ProviderConfig);
        if (pError != null)
        {
            return this.BadRequest(pError);
        }

        if (async)
        {
            await this.queue.PerformAsync(
                this.operationStore,
                this.HttpContext,
                CreateCluster,
                this.logger);
            return this.Accepted();
        }
        else
        {
            CleanRoomCluster cluster = await CreateCluster(NoOpProgressReporter);
            return this.Ok(cluster);
        }

        IActionResult? ValidateCreateInput()
        {
            if (content.AnalyticsWorkloadProfile != null &&
                content.AnalyticsWorkloadProfile.Enabled)
            {
                if (string.IsNullOrEmpty(content.AnalyticsWorkloadProfile.ConfigurationUrl))
                {
                    return this.BadRequest(new ODataError(
                    code: "ConfigurationUrlMissing",
                    message: "A configuration Url must be provided for enabling analytics workload."));
                }
            }

            if (content.InferencingWorkloadProfile?.KServeProfile != null &&
                content.InferencingWorkloadProfile.KServeProfile.Enabled)
            {
                if (string.IsNullOrEmpty(
                    content.InferencingWorkloadProfile.KServeProfile.ConfigurationUrl))
                {
                    return this.BadRequest(new ODataError(
                    code: "ConfigurationUrlMissing",
                    message: "A configuration Url must be provided for enabling inferencing workload."));
                }
            }

            if (content.AadProfile?.AdminGroupObjectIds != null &&
                content.AadProfile.AdminGroupObjectIds.Any())
            {
                foreach (var objectId in content.AadProfile.AdminGroupObjectIds)
                {
                    if (!Guid.TryParse(objectId, out _))
                    {
                        return this.BadRequest(new ODataError(
                            code: "InvalidAdminGroupObjectId",
                            message: $"Admin group object ID '{objectId}' is not a valid GUID."));
                    }
                }
            }

            if (content.FlexNodeProfile != null && content.FlexNodeProfile.Enabled)
            {
                if (string.IsNullOrEmpty(content.FlexNodeProfile.PolicySigningCertPem))
                {
                    return this.BadRequest(new ODataError(
                        code: "SigningCertMissing",
                        message: "Signing certificate PEM is required for flex node deployment."));
                }

                if (content.FlexNodeProfile.NodeCount < 1)
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidNodeCount",
                        message: "Flex node count must be greater than 0."));
                }
            }

            return null;
        }

        async Task<CleanRoomCluster> CreateCluster(IProgress<string> progressReporter)
        {
            return await provider.CreateCluster(
                clusterName,
                clusterInput,
                content.ProviderConfig,
                progressReporter);
        }
    }

    [HttpPost("/clusters/{clusterName}/update")]
    public async Task<IActionResult> UpdateCleanRoomCluster(
        [FromRoute] string clusterName,
        [FromBody] PutClusterInput content,
        [FromQuery] bool async = false)
    {
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);
        var clusterInput = new CleanRoomClusterInput
        {
            ObservabilityProfile = content.ObservabilityProfile,
            MonitoringProfile = content.MonitoringProfile,
            AnalyticsWorkloadProfile = content.AnalyticsWorkloadProfile,
            InferencingWorkloadProfile = content.InferencingWorkloadProfile,
            FlexNodeProfile = content.FlexNodeProfile,
            AadProfile = content.AadProfile
        };
        var pError =
            provider.CreateClusterValidate(clusterName, clusterInput, content.ProviderConfig);
        if (pError != null)
        {
            return this.BadRequest(pError);
        }

        if (async)
        {
            await this.queue.PerformAsync(
                this.operationStore,
                this.HttpContext,
                UpdateCluster,
                this.logger);
            return this.Accepted();
        }
        else
        {
            CleanRoomCluster? cluster = await UpdateCluster(NoOpProgressReporter);
            if (cluster == null)
            {
                return this.NotFound(new ODataError(
                    code: "ClusterNotFound",
                    message: $"No cluster named {clusterName} was found."));
            }

            return this.Ok(cluster);
        }

        IActionResult? ValidateCreateInput()
        {
            // Add any top level input validation.
            if (content.AnalyticsWorkloadProfile != null &&
                content.AnalyticsWorkloadProfile.Enabled)
            {
                if (string.IsNullOrEmpty(content.AnalyticsWorkloadProfile.ConfigurationUrl))
                {
                    return this.BadRequest(new ODataError(
                        code: "ConfigurationUrlMissing",
                        message: "A configuration Url must be provided for enabling analytics workload."));
                }
            }

            if (content.InferencingWorkloadProfile?.KServeProfile != null &&
                content.InferencingWorkloadProfile.KServeProfile.Enabled)
            {
                if (string.IsNullOrEmpty(
                    content.InferencingWorkloadProfile.KServeProfile.ConfigurationUrl))
                {
                    return this.BadRequest(new ODataError(
                        code: "ConfigurationUrlMissing",
                        message: "A configuration Url must be provided for enabling inferencing workload."));
                }
            }

            if (content.AadProfile?.AdminGroupObjectIds != null &&
                content.AadProfile.AdminGroupObjectIds.Any())
            {
                foreach (var objectId in content.AadProfile.AdminGroupObjectIds)
                {
                    if (!Guid.TryParse(objectId, out _))
                    {
                        return this.BadRequest(new ODataError(
                            code: "InvalidAdminGroupObjectId",
                            message: $"Admin group object ID '{objectId}' is not a valid GUID."));
                    }
                }
            }

            if (content.FlexNodeProfile != null && content.FlexNodeProfile.Enabled)
            {
                if (string.IsNullOrEmpty(content.FlexNodeProfile.PolicySigningCertPem))
                {
                    return this.BadRequest(new ODataError(
                        code: "SigningCertMissing",
                        message: "Signing certificate PEM is required for flex node deployment."));
                }

                if (content.FlexNodeProfile.NodeCount < 1)
                {
                    return this.BadRequest(new ODataError(
                        code: "InvalidNodeCount",
                        message: "Flex node count must be greater than 0."));
                }
            }

            return null;
        }

        async Task<CleanRoomCluster?> UpdateCluster(IProgress<string> progressReporter)
        {
            return await provider.UpdateCluster(
                clusterName,
                clusterInput,
                content.ProviderConfig,
                progressReporter);
        }
    }

    [HttpPost("/clusters/{clusterName}/get")]
    public async Task<IActionResult> GetCleanRoomCluster(
        [FromRoute] string clusterName,
        [FromBody] GetClusterInput content)
    {
        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);
        var pError = provider.GetClusterValidate(clusterName, content.ProviderConfig);
        if (pError != null)
        {
            return this.BadRequest(pError);
        }

        CleanRoomCluster? cluster =
            await provider.GetCluster(clusterName, content.ProviderConfig);
        if (cluster != null)
        {
            return this.Ok(cluster);
        }

        return this.NotFound(new ODataError(
            code: "ClusterNotFound",
            message: $"No cluster named {clusterName} was found."));
    }

    [HttpPost("/clusters/{clusterName}/delete")]
    public async Task<IActionResult> DeleteCleanRoomCluster(
        [FromRoute] string clusterName,
        [FromBody] GetClusterInput content)
    {
        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);
        var pError = provider.DeleteClusterValidate(clusterName, content.ProviderConfig);
        if (pError != null)
        {
            return this.BadRequest(pError);
        }

        await provider.DeleteCluster(clusterName, content.ProviderConfig);
        return this.Ok();
    }

    [HttpPost("/clusters/{clusterName}/getkubeconfig")]
    public async Task<IActionResult> GetCleanRoomClusterKubeconfig(
        [FromRoute] string clusterName,
        [FromBody] GetClusterKubeConfigInput content)
    {
        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);
        var pError = provider.GetClusterKubeConfigValidate(clusterName, content.ProviderConfig);
        if (pError != null)
        {
            return this.BadRequest(pError);
        }

        var kubeConfig = await provider.GetClusterKubeConfig(
            clusterName,
            content.ProviderConfig,
            content.AccessRole);
        if (kubeConfig != null)
        {
            return this.Ok(kubeConfig);
        }

        return this.NotFound(new ODataError(
            code: "ClusterNotFound",
            message: $"No cluster named {clusterName} was found."));
    }

    [HttpPost("/clusters/{clusterName}/health")]
    public async Task<IActionResult> GetCleanRoomClusterHealth(
        [FromRoute] string clusterName,
        [FromBody] GetClusterInput content)
    {
        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);
        var pError = provider.GetClusterValidate(clusterName, content.ProviderConfig);
        if (pError != null)
        {
            return this.BadRequest(pError);
        }

        var health = await provider.GetClusterHealth(
            clusterName,
            content.ProviderConfig);
        if (health != null)
        {
            return this.Ok(health);
        }

        return this.NotFound(new ODataError(
            code: "ClusterNotFound",
            message: $"No cluster named {clusterName} was found."));
    }

    [HttpPost("/clusters/analyticsWorkload/generateDeployment")]
    public async Task<IActionResult> GenerateAnalyticsDeployment(
        [FromBody] GenerateAnalyticsWorkloadDeploymentInput content)
    {
        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);

        SecurityPolicyCreationOption policyOption = ToOptionOrDefault(content.SecurityPolicy);
        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        var result = await provider.GenerateAnalyticsWorkloadDeployment(
            new CleanRoomProvider.GenerateAnalyticsWorkloadDeploymentInput
            {
                ContractUrl = content.ContractUrl!,
                TelemetryProfile = content.TelemetryProfile,
                ContractUrlCaCert = content.ContractUrlCaCert,
                SecurityPolicy = content.SecurityPolicy,
                ContractUrlHeaders = content.ContractUrlHeaders
            },
            content.ProviderConfig);

        return this.Ok(result);

        IActionResult? ValidateCreateInput()
        {
            if (policyOption == SecurityPolicyCreationOption.userSupplied)
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidInput",
                    message: $"securityPolicyCreationOption {policyOption} is not applicable."));
            }

            if (string.IsNullOrEmpty(content.ContractUrl))
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidInput",
                    message: $"contractUrl must be specified."));
            }

            return null;
        }
    }

    [HttpPost("/clusters/kserveInferencingWorkload/generateDeployment")]
    public async Task<IActionResult> GenerateKServeInferencingDeployment(
        [FromBody] GenerateKServeInferencingWorkloadDeploymentInput content)
    {
        ClusterProvider provider = this.GetCleanRoomClusterProvider(content.InfraType);

        var error = ValidateCreateInput();
        if (error != null)
        {
            return error;
        }

        var result = await provider.GenerateKServeInferencingWorkloadDeployment(
            new CleanRoomProvider.GenerateKServeInferencingWorkloadDeploymentInput
            {
                ContractUrl = content.ContractUrl!,
                TelemetryProfile = content.TelemetryProfile,
                ContractUrlCaCert = content.ContractUrlCaCert,
                ContractUrlHeaders = content.ContractUrlHeaders,
                SecurityPolicy = content.SecurityPolicy
            },
            content.ProviderConfig);

        return this.Ok(result);

        IActionResult? ValidateCreateInput()
        {
            if (string.IsNullOrEmpty(content.ContractUrl))
            {
                return this.BadRequest(new ODataError(
                    code: "InvalidInput",
                    message: $"contractUrl must be specified."));
            }

            return null;
        }
    }

    private static SecurityPolicyCreationOption ToOptionOrDefault(SecurityPolicyConfigInput? input)
    {
        if (input != null && !string.IsNullOrEmpty(input.PolicyCreationOption))
        {
            return Enum.Parse<SecurityPolicyCreationOption>(
            input.PolicyCreationOption,
            ignoreCase: true);
        }

        return SecurityPolicyCreationOption.cached;
    }
}