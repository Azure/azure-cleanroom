// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry;

namespace Controllers;

[ApiController]
public class InferenceServiceController : InferencingClientBaseController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly FrontendClientManager frontendClientManager;

    public InferenceServiceController(
        ILogger logger,
        IConfiguration configuration,
        FrontendClientManager clientManager,
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

        await this.CheckConsortiumMembership();

        ValidateInputs(input);

        Baggage.SetBaggage(BaggageItemName.ModelDocumentId, input.ModelId);

        // Run the approval/consent gate before any other governance work so
        // that authorization errors take precedence over input-shape errors.
        // The model and dataset documents fetched here are reused by the
        // conversion step to avoid a second round trip.
        var (modelDoc, datasetDocs) = await this.CheckModelApproved(input.ModelId, name);

        var frontendClient = await this.frontendClientManager.GetClient();

        FrontendJobInput frontendJob =
            await this.ConvertToFrontendJob(input, modelDoc, datasetDocs, name);

        var telemetryStatus = await this.GetRuntimeConsent(input.ModelId, "telemetry");
        if (telemetryStatus.Status != "enabled")
        {
            this.logger.LogWarning(
                $"Telemetry runtime consent for model '{name}' " +
                $"is disabled: {telemetryStatus.Reason.Code}: {telemetryStatus.Reason.Message}");
        }

        bool enableTelemetry = telemetryStatus.Status == "enabled";

        await this.SetupInferencingServicePodsAccess(frontendJob, enableTelemetry);

        await this.SetInferencingFrontendAsPodPolicyAdmin();

        await this.GovernanceClientManager.GetClient().LogAuditEventAsync(
            $"Starting inference service deployment for '{name}' bound to " +
            $"model document '{input.ModelId}'.",
            this.logger,
            "kserve-inferencing-agent");

        using var response = await frontendClient.PostAsync(
            "/inferencing/deployModel",
            JsonContent.Create(new
            {
                Job = frontendJob,
                enableTelemetryCollection = enableTelemetry
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

            if (string.IsNullOrWhiteSpace(input.Predictor.Model.Runtime))
            {
                ThrowBadRequest(
                    "RuntimeMissing",
                    "The predictor model 'runtime' must be specified.");
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

    // Verifies that the model document has been approved by the owner of
    // the dataset it references and that 'execution' runtime consent is
    // enabled on the model and dataset documents. Mirrors the analytics
    // agent's CheckQueryApproved gate. Returns the fetched model and
    // dataset documents so callers don't need to refetch them.
    private async Task<(
        UserDocument<InferencingModelSpecification> ModelDoc,
        List<UserDocument<Dataset>> DatasetDocs)>
        CheckModelApproved(
            string modelDocumentId,
            string name)
    {
        var modelDoc = await this.GetUserDocument<InferencingModelSpecification>(modelDocumentId);

        this.logger.LogInformation(
            $"Checking if model document '{modelDocumentId}' is approved by " +
            "the dataset owners.");

        var datasetRefs = modelDoc.Data.Application.ModelDatasets;
        if (datasetRefs == null || datasetRefs.Count == 0)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "ModelDatasetsMissing",
                    message: $"Model document '{modelDocumentId}' must " +
                    "specify at least one 'modelDatasets[].specification'."));
        }

        foreach (var datasetRef in datasetRefs)
        {
            if (datasetRef == null ||
                string.IsNullOrWhiteSpace(datasetRef.Specification))
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "ModelDatasetsMissing",
                        message: $"Model document '{modelDocumentId}' has a " +
                        "'modelDatasets' entry without 'specification'."));
            }
        }

        var datasetDocs = new List<UserDocument<Dataset>>(datasetRefs.Count);
        foreach (var datasetRef in datasetRefs)
        {
            datasetDocs.Add(await this.GetUserDocument<Dataset>(datasetRef.Specification));
        }

        List<string> documentsToConsentCheck = [modelDocumentId];
        documentsToConsentCheck.AddRange(datasetDocs.Select(d => d.Id));

        var approvedBy = modelDoc.FinalVotes
            .Where(x => x.Ballot == Ballot.Accepted)
            .Select(x => x.ApproverId)
            .ToHashSet();
        var missingOwners = datasetDocs
            .Where(d => !approvedBy.Contains(d.ProposerId))
            .Select(d => d.Id)
            .ToList();
        if (missingOwners.Count > 0)
        {
            var missingList = string.Join("', '", missingOwners);
            await this.GovernanceClientManager.GetClient().LogAuditEventAsync(
                $"Inference service deployment denied for '{name}': missing " +
                $"approval from dataset owner(s) of '{missingList}'.",
                this.logger,
                "kserve-inferencing-agent");
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "ModelMissingApprovalFromDatasetOwner",
                    message: $"Model '{modelDocumentId}' requires approval from " +
                    $"the owner(s) of dataset(s) '{missingList}'."));
        }

        this.logger.LogInformation(
            $"Checking model '{modelDocumentId}' and its datasets have " +
            "execution consent enabled.");
        foreach (var docId in documentsToConsentCheck)
        {
            var status = await this.GetRuntimeConsent(docId, "execution");
            if (status.Status != "enabled")
            {
                await this.GovernanceClientManager.GetClient().LogAuditEventAsync(
                    $"Inference service deployment denied for '{name}'. " +
                    $"Reason: {status.Reason.Message}.",
                    this.logger,
                    "kserve-inferencing-agent");
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: status.Reason.Code,
                        message: status.Reason.Message));
            }
        }

        return (modelDoc, datasetDocs);
    }

    // Converts the API input to the frontend's expected shape using the
    // governed model and dataset documents previously fetched by the
    // approval gate. The runtime is supplied by the REST input (required)
    // and, if the model document also pins a runtime name, the two must
    // match. The implementing image+digest is resolved by the frontend
    // from its bundled digest table (operationally pinned by release
    // version), not user-supplied input.
    private async Task<FrontendJobInput> ConvertToFrontendJob(
        ModelInput input,
        UserDocument<InferencingModelSpecification> modelDoc,
        List<UserDocument<Dataset>> datasetDocs,
        string name)
    {
        string callerRuntime = input.Predictor.Model.Runtime;

        // If the model document carries an 'application.runtime' object,
        // it must have a non-empty 'name' — a present-but-empty entry is
        // treated as a malformed governance document, never as "no
        // pinned runtime", so it cannot silently bypass the match check.
        var runtime = modelDoc.Data.Application.Runtime;
        if (runtime != null)
        {
            if (string.IsNullOrWhiteSpace(runtime.Name))
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "ModelRuntimeInvalid",
                        message: $"Model document '{modelDoc.Id}' has " +
                        "'application.runtime' set without a non-empty 'name'."));
            }

            if (!string.Equals(callerRuntime, runtime.Name, StringComparison.Ordinal))
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "RuntimeMismatch",
                        message: $"Predictor runtime '{callerRuntime}' does " +
                        $"not match runtime '{runtime.Name}' " +
                        $"bound by model document '{modelDoc.Id}'."));
            }
        }

        // The REST input is authoritative for the runtime selected at
        // deploy time; the model document (when it pins one) is a
        // governance constraint that the caller's choice must satisfy.
        string resolvedRuntime = callerRuntime;

        var datasets = datasetDocs
            .Select(d => new DatasetInfo(
                Name: d.Data.Name,
                ViewName: d.Data.Name,
                OwnerId: d.ProposerId,
                Format: d.Data.Schema.Format,
                Schema: d.Data.Schema.Fields.ToDictionary(
                    k => k.Name,
                    v => new SchemaFieldType(v.Type)),
                AccessPoint: d.Data.AccessPoint,
                AllowedFields: d.Data.Policy.AllowedFields?.ToList() ?? []))
            .ToList();

        var frontendModel = new FrontendModelInput(
            new FrontendModelFormatInput(
                input.Predictor.Model.ModelFormat.Name,
                input.Predictor.Model.ModelFormat.Version),
            input.Predictor.Model.ProtocolVersion,
            resolvedRuntime,
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
            input.Predictor.AutoScaling,
            input.Predictor.Affinity);

        // Map placement from platform placement + predictor spec.
        FrontendPlacementInput? frontendPlacement = null;
        bool? hostNetwork = input.Placement?.HostNetwork;
        if (hostNetwork != null)
        {
            frontendPlacement = new FrontendPlacementInput(hostNetwork);
        }

        var govJobInput = await this.GetGovernanceJobInput();

        var frontendJobInput = new FrontendJobInput(
            modelDoc.ContractId,
            name,
            frontendPredictor,
            modelDoc.Data.Application.ModelDir,
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

    private async Task SetupInferencingServicePodsAccess(
        FrontendJobInput job, bool enableTelemetryCollection)
    {
        List<Task> setupTasks = [];
        HashSet<string> subjects = [];
        HashSet<string> secretIds = [];
        InferencingServicePolicy svcPolicy =
            await this.GetInferencingPodsPolicy(job, enableTelemetryCollection);
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
        GetInferencingPodsPolicy(FrontendJobInput job, bool enableTelemetryCollection)
    {
        var frontendClient = await this.frontendClientManager.GetClient();

        using var response = await frontendClient.PostAsync(
            "inferencing/generateSecurityPolicy",
            JsonContent.Create(new
            {
                Job = job,
                enableTelemetryCollection
            }));

        await response.ValidateStatusCodeAsync(this.logger);
        var jobPolicy =
            (await response.Content.ReadFromJsonAsync<InferencingServicePolicy>())!;
        return jobPolicy;
    }
}
