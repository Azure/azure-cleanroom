// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

[ApiController]
public class InferenceServiceController : InferencingClientBaseController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly InferencingFrontendClientManager frontendClientManager;

    public InferenceServiceController(
        ILogger logger,
        IConfiguration configuration,
        InferencingFrontendClientManager clientManager,
        ActiveUserChecker activeUserChecker,
        GovernanceClientManager governanceClientManager)
        : base(logger, configuration, activeUserChecker, governanceClientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.frontendClientManager = clientManager;
    }

    [HttpPost("/inferenceServices")]
    public async Task<IActionResult> CreateOrUpdate(
        [FromBody] ModelInput input)
    {
        string name = input.Name;
        this.logger.LogInformation(
            $"Preparing inference service deployment for '{name}'.");

        await this.CheckCallerAuthorized();

        // TODO (gsinha): Enable consortium membership check if required.
        ////await this.CheckConsortiumMembership();

        ValidateInputs(input);

        var frontendClient = await this.frontendClientManager.GetClient();

        FrontendJobInput frontendJob =
            await this.ConvertToFrontendJob(input);

        await this.SetupInferencingServicePodsAccess(frontendJob);

        await this.SetInferencingFrontendAsPodPolicyAdmin();

        await this.GovernanceClientManager.GetClient().LogAuditEventAsync(
            $"Starting inference service deployment for: {name}.",
            this.logger);

        // TODO (gsinha): Figure out baggage items.
        ////Baggage.SetBaggage(BaggageItemName.RunId, runId);
        ////Baggage.SetBaggage(BaggageItemName.QueryId, queryId);
        using var response = await frontendClient.PostAsync(
            "/inferencing/deployModel",
            JsonContent.Create(new
            {
                Job = frontendJob,
                enableTelemetryCollection = true
            }));
        await response.ValidateStatusCodeAsync(this.logger);
        var submissionResult =
            (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        this.logger.LogInformation(
            $"Inference service '{name}' deployment submitted: " +
            $"{JsonSerializer.Serialize(submissionResult)}.");

        return this.Ok(submissionResult);

        static void ValidateInputs(ModelInput input)
        {
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                ThrowBadRequest(
                    "InferenceServiceNameMissing",
                    "The inference service 'name' must be specified.");
            }

            if (string.IsNullOrWhiteSpace(input.ModelId))
            {
                ThrowBadRequest(
                    "ModelIdMissing",
                    "The 'modelId' must be specified.");
            }

            if (string.IsNullOrWhiteSpace(input.Predictor.Model.ModelFormat.Name))
            {
                ThrowBadRequest(
                    "ModelFormatNameMissing",
                    "The predictor model 'modelFormat.name' must be specified.");
            }

            if (input.Predictor.Model.ProtocolVersion != null &&
                string.IsNullOrWhiteSpace(input.Predictor.Model.ProtocolVersion))
            {
                ThrowBadRequest(
                    "ProtocolVersionInvalid",
                    "The predictor model 'protocolVersion' cannot be empty.");
            }

            if (input.Predictor.Model.Runtime != null &&
                string.IsNullOrWhiteSpace(input.Predictor.Model.Runtime))
            {
                ThrowBadRequest(
                    "RuntimeInvalid",
                    "The predictor model 'runtime' cannot be empty.");
            }

            if (input.Predictor.Model.StorageUri != null &&
                string.IsNullOrWhiteSpace(input.Predictor.Model.StorageUri))
            {
                ThrowBadRequest(
                    "StorageUriInvalid",
                    "The predictor model 'storageUri' cannot be empty.");
            }

            if (input.Predictor.MinReplicas < 0)
            {
                ThrowBadRequest(
                    "MinReplicasInvalid",
                    "The predictor 'minReplicas' cannot be negative.");
            }

            if (input.Predictor.MaxReplicas != null &&
                input.Predictor.MaxReplicas <= 0)
            {
                ThrowBadRequest(
                    "MaxReplicasInvalid",
                    "The predictor 'maxReplicas' must be greater than 0.");
            }

            if (input.Predictor.MinReplicas != null &&
                input.Predictor.MaxReplicas != null &&
                input.Predictor.MinReplicas > input.Predictor.MaxReplicas)
            {
                ThrowBadRequest(
                    "ReplicaRangeInvalid",
                    "The predictor 'minReplicas' cannot be greater than 'maxReplicas'.");
            }

            if (input.Predictor.Model.Args?.Any(string.IsNullOrWhiteSpace) == true)
            {
                ThrowBadRequest(
                    "ModelArgsInvalid",
                    "The predictor model 'args' cannot contain empty values.");
            }

            if (input.Predictor.Timeout != null && input.Predictor.Timeout <= 0)
            {
                ThrowBadRequest(
                    "TimeoutInvalid",
                    "The predictor 'timeout' must be greater than 0.");
            }

            if (input.Predictor.Batcher != null)
            {
                if (input.Predictor.Batcher.MaxBatchSize != null &&
                    input.Predictor.Batcher.MaxBatchSize <= 0)
                {
                    ThrowBadRequest(
                        "BatcherMaxBatchSizeInvalid",
                        "The predictor batcher 'maxBatchSize' must be greater than 0.");
                }

                if (input.Predictor.Batcher.MaxLatency != null &&
                    input.Predictor.Batcher.MaxLatency <= 0)
                {
                    ThrowBadRequest(
                        "BatcherMaxLatencyInvalid",
                        "The predictor batcher 'maxLatency' must be greater than 0.");
                }

                if (input.Predictor.Batcher.Timeout != null &&
                    input.Predictor.Batcher.Timeout <= 0)
                {
                    ThrowBadRequest(
                        "BatcherTimeoutInvalid",
                        "The predictor batcher 'timeout' must be greater than 0.");
                }
            }

            if (input.Predictor.Model.Env?.Any(e =>
                string.IsNullOrWhiteSpace(e.Name)) == true)
            {
                ThrowBadRequest(
                    "ModelEnvInvalid",
                    "The predictor model 'env' entries must have a non-empty 'name'.");
            }

            if (input.Predictor.ScaleMetricType != null)
            {
                var validTypes = new[] { "Utilization", "AverageValue" };
                if (!validTypes.Contains(input.Predictor.ScaleMetricType))
                {
                    ThrowBadRequest(
                        "ScaleMetricTypeInvalid",
                        "The predictor 'scaleMetricType' must be " +
                        "'Utilization' or 'AverageValue'.");
                }
            }

            if (input.Predictor.AutoScaling?.Metrics?.Any(m =>
                string.IsNullOrWhiteSpace(m.Type)) == true)
            {
                ThrowBadRequest(
                    "AutoScalingMetricTypeInvalid",
                    "The predictor autoScaling metrics 'type' must " +
                    "be specified.");
            }
        }

        static void ThrowBadRequest(string code, string message)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: code,
                    message: message));
        }
    }

    [HttpGet("/inferenceServices/{name}/status")]
    public async Task<JsonObject> GetStatus([FromRoute] string name)
    {
        // Since this API can be called quite frequently to track the status
        // use the cache to avoid repeatedly querying governance endpoint.
        await this.CheckCallerAuthorized(useCache: true);

        var frontendClient = await this.frontendClientManager.GetClient();
        using var response = await frontendClient.GetAsync(
            $"/inferencing/status/{name}");
        await response.ValidateStatusCodeAsync(this.logger);
        var content =
            (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return content;
    }

    // Converts the API input to the frontend's expected shape,
    // enriching with governance data from CGS documents.
    private async Task<FrontendJobInput> ConvertToFrontendJob(
        ModelInput input)
    {
        string name = input.Name;

        var govJobInput = await this.GetGovernanceJobInput();

        var frontendModel = new FrontendModelInput(
            new FrontendModelFormatInput(
                input.Predictor.Model.ModelFormat.Name,
                input.Predictor.Model.ModelFormat.Version),
            input.Predictor.Model.ProtocolVersion,
            input.Predictor.Model.Runtime,
            input.Predictor.Model.StorageUri,
            input.Predictor.Model.Args,
            input.Predictor.Model.Resources,
            input.Predictor.Model.Env,
            input.Predictor.Model.Storage);

        FrontendBatcherInput? frontendBatcher = null;
        if (input.Predictor.Batcher != null)
        {
            frontendBatcher = new FrontendBatcherInput(
                input.Predictor.Batcher.MaxBatchSize,
                input.Predictor.Batcher.MaxLatency,
                input.Predictor.Batcher.Timeout);
        }

        var frontendPredictor = new FrontendPredictorInput(
            frontendModel,
            input.Predictor.MinReplicas,
            input.Predictor.MaxReplicas,
            input.Predictor.Timeout,
            frontendBatcher,
            input.Predictor.DeploymentStrategy,
            input.Predictor.ScaleMetricType,
            input.Predictor.AutoScaling);

        // Map placement from platform placement + predictor spec.
        FrontendPlacementInput? frontendPlacement = null;
        bool? hostNetwork = input.Placement?.HostNetwork;
        if (hostNetwork != null)
        {
            frontendPlacement = new FrontendPlacementInput(hostNetwork);
        }

        // Retrieve model document and datasets from CGS if modelId
        // is provided.
        string? contractId = null;
        string? modelDir = null;
        List<DatasetInfo> datasets = [];

        if (!string.IsNullOrEmpty(input.ModelId))
        {
            var modelDoc =
                await this.GetUserDocument<InferencingModelSpecification>(
                    input.ModelId);
            contractId = modelDoc.ContractId;
            modelDir = modelDoc.Data.Application.ModelDir;

            foreach (var datasetRef in
                modelDoc.Data.Application.InputDataset)
            {
                var datasetDoc =
                    await this.GetUserDocument<Dataset>(
                        datasetRef.Specification);
                DatasetInfo datasetInfo = new(
                    Name: datasetDoc.Data.Name,
                    ViewName: datasetDoc.Data.Name,
                    OwnerId: datasetDoc.ProposerId,
                    Format: datasetDoc.Data.Schema.Format,
                    Schema: datasetDoc.Data.Schema.Fields.ToDictionary(
                        k => k.Name,
                        v => new SchemaFieldType(v.Type)),
                    AccessPoint: datasetDoc.Data.AccessPoint,
                    AllowedFields:
                        datasetDoc.Data.Policy.AllowedFields?.ToList()
                        ?? []);
                datasets.Add(datasetInfo);
            }
        }

        if (string.IsNullOrEmpty(contractId))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "ContractIdMissing",
                    message: "Could not determine contractId. " +
                    "Provide a modelId that references a governance " +
                    "document with a contractId."));
        }

        var frontendJobInput = new FrontendJobInput(
            contractId,
            name,
            frontendPredictor,
            modelDir,
            datasets,
            govJobInput,
            frontendPlacement);

        this.logger.LogInformation(
            $"Frontend job input for '{name}': " +
            $"{JsonSerializer.Serialize(frontendJobInput)}");

        return frontendJobInput;
    }

    private async Task<GovernanceJobInput> GetGovernanceJobInput()
    {
        var client = this.GovernanceClientManager.GetClient();
        var gc = (await client.GetFromJsonAsync<GovernanceConfig>("/show"))!;

        if (string.IsNullOrEmpty(gc.CcrgovEndpoint))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "GovernanceEndpointNotSpecified",
                    message: "No governance endpoint was retrieved."));
        }

        string? serviceCert = gc.ServiceCert;
        string? serviceCertBase64 = null;
        if (string.IsNullOrEmpty(serviceCert) &&
            gc.ServiceCertDiscovery == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "ServiceCertNotSpecified",
                    message: "No service cert or cert discovery " +
                    "information was retrieved for the " +
                    "governance endpoint."));
        }

        if (gc.ServiceCertDiscovery == null)
        {
            serviceCertBase64 = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(serviceCert!));
        }

        return new GovernanceJobInput(
            gc.CcrgovEndpoint,
            serviceCertBase64,
            gc.ServiceCertDiscovery);
    }

    private async Task SetupInferencingServicePodsAccess(FrontendJobInput job)
    {
        List<Task> setupTasks = [];
        HashSet<string> subjects = [];
        HashSet<string> secretIds = [];
        InferencingServicePolicy svcPolicy =
            await this.GetInferencingPodsPolicy(job);
        List<DatasetInfo> datasets = [.. job.Datasets];
        foreach (var dataset in datasets)
        {
            switch (dataset.AccessPoint.Store.Type)
            {
                case ResourceType.Azure_BlobStorage:
                    if (dataset.AccessPoint.Protection.EncryptionSecrets
                        == null)
                    {
                        this.logger.LogInformation(
                            $"No encryption secrets for the specified " +
                            $"dataset {dataset.Name}.");
                    }
                    else
                    {
                        secretIds.Add(
                            dataset.AccessPoint.Protection
                            .EncryptionSecrets.Dek.Secret
                            .BackingResource.Name);
                    }

                    subjects.Add(string.Join(
                        "-",
                        job.ContractId,
                        dataset.OwnerId));
                    break;

                case ResourceType.Aws_S3:
                    var providerConfig =
                        dataset.AccessPoint.Store.Provider.Configuration;
                    var config = JsonSerializer.Deserialize<JsonObject>(
                        Encoding.UTF8.GetString(
                            Convert.FromBase64String(providerConfig)))!;
                    secretIds.Add(config["secretId"]!.ToString());
                    break;

                default:
                    throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "UnsupportedAccessPointStoreType",
                        message: "Access point store type " +
                        $"'{dataset.AccessPoint.Store.Type}' " +
                        "is not supported."));
            }
        }

        foreach (var secretId in secretIds)
        {
            setupTasks.Add(
                this.SetSecretAccessPolicy(secretId, svcPolicy));
        }

        foreach (var subject in subjects)
        {
            setupTasks.Add(
                this.SetIdpTokenAccessPolicy(subject, svcPolicy));
        }

        setupTasks.Add(this.SetEventsEmissionPolicy(svcPolicy));
        setupTasks.Add(this.SetEndorsedCertPolicy(svcPolicy));

        await Task.WhenAll(setupTasks);
    }

    private async Task<InferencingServicePolicy>
        GetInferencingPodsPolicy(FrontendJobInput job)
    {
        var frontendClient =
            await this.frontendClientManager.GetClient();

        // TODO (GSinha): Check the model documents runtime options
        // to see if telemetry is enabled and pass it in.
        using var response = await frontendClient.PostAsync(
            "inferencing/generateSecurityPolicy",
            JsonContent.Create(new
            {
                Job = job,
                enableTelemetryCollection = true
            }));

        await response.ValidateStatusCodeAsync(this.logger);
        var jobPolicy = (await response.Content
            .ReadFromJsonAsync<InferencingServicePolicy>())!;
        return jobPolicy;
    }
}
