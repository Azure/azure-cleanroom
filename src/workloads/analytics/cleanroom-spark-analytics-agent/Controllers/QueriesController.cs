// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Data;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using CcrSecrets;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry;
using Polly;

namespace Controllers;

[ApiController]
public class QueriesController : AnalyticsClientBaseController
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly SparkFrontendClientManager frontendClientManager;
    private readonly SecretsClient secretsClient;
    private readonly Dictionary<string, object> retryContextData;
    private readonly string dateFormat = "yyyy-MM-dd";

    public QueriesController(
        ILogger logger,
        IConfiguration configuration,
        SparkFrontendClientManager clientManager,
        ActiveUserChecker activeUserChecker,
        GovernanceClientManager governanceClientManager)
        : base(logger, configuration, activeUserChecker, governanceClientManager)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.frontendClientManager = clientManager;
        this.secretsClient = new SecretsClient(this.logger, this.configuration);
        this.retryContextData = new Dictionary<string, object>
        {
            {
                "logger",
                this.logger
            }
        };
    }

    [HttpPost("/queries/{queryId}/generateSecurityPolicy")]
    public async Task<IActionResult> GeneratePolicy([FromRoute] string queryId)
    {
        this.logger.LogInformation($"Preparing for query submission for queryId '{queryId}'.");
        await this.CheckCallerAuthorized();
        Baggage.SetBaggage(BaggageItemName.QueryId, queryId);

        JobInput frontendJob = await this.ConvertJob(queryId, runInput: null);
        JobPolicy jobPolicy = await this.GetSqlJobPolicy(frontendJob);
        return this.Ok(jobPolicy);
    }

    [HttpPost("/queries/{queryId}/run")]
    public async Task<IActionResult> RunQuery(
        [FromRoute] string queryId,
        [FromBody] RunQueryInput runInput)
    {
        this.logger.LogInformation($"Preparing for query submission for queryId '{queryId}'.");
        await this.CheckCallerAuthorized();
        await this.CheckConsortiumMembership();
        ValidateInputs(runInput);

        var frontendClient = await this.frontendClientManager.GetClient();
        string runId;
        if (!string.IsNullOrEmpty(runInput.RunId))
        {
            // Check if this run was already submitted.
            var checkJobId = this.FrontendJobIdFormat(runInput.RunId);
            Baggage.SetBaggage(BaggageItemName.RunId, runInput.RunId);
            Baggage.SetBaggage(BaggageItemName.QueryId, queryId);
            using var statusResponse =
                await frontendClient.GetAsync($"/analytics/status/{checkJobId}");
            if (statusResponse.IsSuccessStatusCode)
            {
                var statusResult =
                    (await statusResponse.Content.ReadFromJsonAsync<JsonObject>())!;
                this.logger.LogInformation(
                    $"Query '{queryId}' with run Id '{runInput.RunId}' already submitted: " +
                    $"{JsonSerializer.Serialize(statusResult)}.");
                return this.Ok(statusResult);
            }

            if (statusResponse.StatusCode != HttpStatusCode.NotFound)
            {
                // Let the call fail as this is not an expected status code.
                await statusResponse.ValidateStatusCodeAsync(this.logger);
                throw new Exception(
                    "Not expecting the control to reach here as " +
                    "ValidateStatusCodeAsync should have thrown.");
            }

            // No status for run Id exists so submit the run.
            runId = runInput.RunId;
        }
        else
        {
            runId = Guid.NewGuid().ToString("N").ToLowerInvariant();
        }

        await this.CheckQueryApproved(queryId, runId);
        var telemetryStatus = await this.GetRuntimeConsent(queryId, "telemetry");
        if (telemetryStatus.Status != "enabled")
        {
            this.logger.LogWarning(
                $"Telemetry runtime consent for query '{queryId}' is disabled due to following " +
                $"reason: code: {telemetryStatus.Reason.Code}, " +
                $"message: '{telemetryStatus.Reason.Message}'");
        }

        JobInput frontendJob = await this.ConvertJob(queryId, runInput);
        await this.TransferSecrets(queryId);
        await this.SetupSparkPodsAccess(frontendJob);
        await this.GovernanceClientManager.GetClient().LogAuditEventAsync(
            $"Starting query execution for queryId: {queryId}. | job id: {runId}.",
            this.logger);
        Baggage.SetBaggage(BaggageItemName.RunId, runId);
        Baggage.SetBaggage(BaggageItemName.QueryId, queryId);
        using var response = await frontendClient.PostAsync(
            "/analytics/submitSqlJob",
            JsonContent.Create(new
            {
                JobId = runInput.RunId,
                QueryId = queryId,
                Job = frontendJob,
                enableTelemetryCollection = telemetryStatus.Status == "enabled",
                useOptimizer = runInput.UseOptimizer,
                dryRun = runInput.DryRun,
            }));
        await response.ValidateStatusCodeAsync(this.logger);
        var submissionResult = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        this.logger.LogInformation(
            $"Query '{queryId}' with run Id '{runId}' submitted: " +
            $"{JsonSerializer.Serialize(submissionResult)}.");

        if (!runInput.DryRun)
        {
            var submittedJobId = submissionResult["id"]?.ToString();
            var expectedJobId = this.FrontendJobIdFormat(runId);
            if (submittedJobId != expectedJobId)
            {
                throw new Exception(
                    $"Expecting jobId to be {expectedJobId} but submit job returned job " +
                    $"id {submittedJobId}.");
            }
        }

        return this.Ok(submissionResult);

        void ValidateInputs(RunQueryInput runInput)
        {
            var startDate = runInput.StartDate;
            var endDate = runInput.EndDate;
            if (startDate != null || endDate != null)
            {
                if (startDate == null || endDate == null)
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        new ODataError(
                            code: "InvalidDateRange",
                            message: "Both startDate and endDate must be provided."));
                }

                if (startDate?.TimeOfDay != TimeSpan.Zero || endDate?.TimeOfDay != TimeSpan.Zero)
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        new ODataError(
                            code: "InvalidDateFormat",
                            message: $"startDate and endDate must be in date format " +
                            $"{this.dateFormat} with no timestamp."));
                }

                if (startDate > endDate)
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        new ODataError(
                            code: "InvalidDateRange",
                            message: "startDate must be earlier than or equal to the endDate."));
                }
            }
        }
    }

    [HttpGet("/status/{jobId}")]
    public async Task<JsonObject> GetStatus([FromRoute] string jobId)
    {
        // Since this API can be called quiet frequently to track the status use the cache to
        // avoid repeatedly querying governance endpoint.
        await this.CheckCallerAuthorized(useCache: true);

        var frontendClient = await this.frontendClientManager.GetClient();
        using var response = await frontendClient.GetAsync($"/analytics/status/{jobId}");
        await response.ValidateStatusCodeAsync(this.logger);
        var content = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return content;
    }

    [HttpGet("/queries/{queryId}/runs")]
    public async Task<JsonObject> GetRuns([FromRoute] string queryId)
    {
        await this.CheckCallerAuthorized(useCache: true);

        var frontendClient = await this.frontendClientManager.GetClient();
        using var response = await frontendClient.GetAsync($"/analytics/{queryId}/runs");
        await response.ValidateStatusCodeAsync(this.logger);
        var content = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        return content;
    }

    private async Task CheckQueryApproved(string queryId, string runId)
    {
        List<string> documentsToConsentCheck = [queryId];
        var queryDocument =
            await this.GetUserDocument<SparkApplicationSpecification>(queryId);

        this.logger.LogInformation(
            $"Checking if query '{queryId}' is approved by the required dataset owners.");

        if (queryDocument.Data.Application.InputDataset == null ||
            queryDocument.Data.Application.InputDataset.Count == 0)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "DatasetMissing",
                    message: $"Atleast one dataset must be specified."));
        }

        List<UserDocument<Dataset>> datasets = [];
        foreach (var dataset in queryDocument.Data.Application.InputDataset)
        {
            documentsToConsentCheck.Add(dataset.Specification);
            var datasetDoc = await this.GetUserDocument<Dataset>(dataset.Specification);
            datasets.Add(datasetDoc);
        }

        var approvedBy =
            queryDocument.FinalVotes.Where(
                x => x.Ballot == Ballot.Accepted).Select(x => x.ApproverId).ToList();
        var missingApprovals = datasets.Where(d => !approvedBy.Contains(d.ProposerId))
            .Select(d => d.Id).ToList();
        if (missingApprovals.Count > 0)
        {
            await this.GovernanceClientManager.GetClient().LogAuditEventAsync(
                $"Query execution denied for queryId {queryId}: Missing approvals from " +
                $"dataset owners: {string.Join(", ", missingApprovals)} | job id: {runId}.",
                this.logger);
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "QueryMissingApprovalsFromDatasetOwners",
                    message: $"Query '{queryId}' requires approvals from the owners of the " +
                    $"following datasets: {string.Join(", ", missingApprovals)}"));
        }

        this.logger.LogInformation(
            $"Checking query '{queryId}' and its datasets have execution consent enabled.");
        foreach (var docId in documentsToConsentCheck)
        {
            var status = await this.GetRuntimeConsent(docId, "execution");
            if (status.Status != "enabled")
            {
                await this.GovernanceClientManager.GetClient().LogAuditEventAsync(
                    $"Query execution denied for queryId {queryId}. " +
                    $"Reason: {status.Reason.Message} | job id: {runId}.",
                    this.logger);
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: status.Reason.Code,
                        message: status.Reason.Message));
            }
        }
    }

    private async Task TransferSecrets(string queryId)
    {
        JobInput inputJob = await this.GetSqlJobInput(queryId, runInput: null);
        List<DatasetInfo> datasets = [.. inputJob.Datasets, inputJob.Datasink];
        List<Task> transferTasks = [];
        foreach (var dataset in datasets)
        {
            transferTasks.Add(TransferDatasetSecret(dataset));
        }

        await Task.WhenAll(transferTasks);

        async Task TransferDatasetSecret(DatasetInfo dataset)
        {
            switch (dataset.AccessPoint.Store.Type)
            {
                case ResourceType.Azure_BlobStorage:
                case ResourceType.Azure_BlobStorage_DataLakeGen2:
                    await TransferBlobStorageSecret();
                    break;

                case ResourceType.Aws_S3:
                    // No action required.
                    break;

                default:
                    throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "UnsupportedAccessPointStoreType",
                        message: $"Access point store type '{dataset.AccessPoint.Store.Type}' " +
                        $"is not supported for query execution."));
            }

            async Task TransferBlobStorageSecret()
            {
                if (dataset.AccessPoint.Protection.EncryptionSecrets == null)
                {
                    this.logger.LogInformation($"No setup for access required as there are no " +
                        $"encryption secrets for dataset {dataset.Name}");
                    return;
                }

                this.logger.LogInformation($"Setting up access for dataset {dataset.Name}.");

                // Get access token(s) to access AKV endpoints for DEK/KEK, unwrap the Dek and
                // transfer it over as a CCF secret and set secret access policy for the
                // driver/executor pods.
                var encSecrets = dataset.AccessPoint.Protection.EncryptionSecrets;
                var accessIdentity = dataset.AccessPoint.Protection.EncryptionSecretAccessIdentity!;
                this.ValidateDataset(encSecrets, accessIdentity);

                var providerConfig =
                    encSecrets.Kek!.Secret.BackingResource.Provider.Configuration!;
                var config = JsonSerializer.Deserialize<JsonObject>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(providerConfig)))!;
                var maaEndpoint = config["authority"]?.ToString();
                if (string.IsNullOrEmpty(maaEndpoint))
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        new ODataError(
                            code: "KekProviderConfigurationAuthorityMissing",
                            message:
                            "KEK provider configuration MAA endoint/authority value is missing."));
                }

                string subject = string.Join("-", inputJob.ContractId, dataset.OwnerId);
                string? issuer = accessIdentity.TokenIssuer?.Issuer?.Url;
                string kekAccessToken = await GetAccessToken(
                    accessIdentity.ClientId,
                    accessIdentity.TenantId,
                    subject,
                    issuer,
                    scope: encSecrets.Kek.Secret.BackingResource.Provider.Url.ToLower()
                        .Contains("vault.azure.net") ?
                        "https://vault.azure.net/.default" :
                        "https://managedhsm.azure.net/.default");
                string dekAccessToken = await GetAccessToken(
                    accessIdentity.ClientId,
                    accessIdentity.TenantId,
                    subject,
                    issuer,
                    scope: "https://vault.azure.net/.default");

                byte[] dek = await this.secretsClient.UnwrapSecret(new UnwrapSecretRequest
                {
                    ClientId = accessIdentity.ClientId,
                    TenantId = accessIdentity.TenantId,
                    Kid = encSecrets.Dek.Secret.BackingResource.Name,
                    AccessToken = dekAccessToken,
                    AkvEndpoint = encSecrets.Dek.Secret.BackingResource.Provider.Url,
                    Kek = new KekInfo
                    {
                        AccessToken = kekAccessToken,
                        AkvEndpoint = encSecrets.Kek.Secret.BackingResource.Provider.Url,
                        Kid = encSecrets.Kek.Secret.BackingResource.Name,
                        MaaEndpoint = maaEndpoint
                    }
                });

                var secretName =
                    this.ToCgsSecretName(queryId, encSecrets.Dek.Secret.BackingResource);
                var secretId =
                    await this.CreateSecret(secretName, Convert.ToBase64String(dek));
                if (secretId != this.ToCgsSecretId(secretName))
                {
                    throw new ApiException(new ODataError(
                        code: "UnexpectedSecretIdFormat",
                        message: $"SecretId value was expected to be " +
                        $"'{this.ToCgsSecretId(secretName)}' " +
                        $"but value returned by CGS is {secretId}."));
                }
            }

            async Task<string> GetAccessToken(
                string clientId,
                string tenantId,
                string sub,
                string? issuer,
                string scope)
            {
                var aud = "api://AzureADTokenExchange";
                TokenCredential credential = new ClientAssertionCredential(
                    tenantId,
                    clientId,
                    async (cToken) =>
                    {
                        var govClient = this.GovernanceClientManager.GetClient();
                        string url = $"/oauth/token?sub={sub}&tenantId={tenantId}&aud={aud}";
                        if (!string.IsNullOrEmpty(issuer))
                        {
                            url += $"&iss={issuer}";
                        }

                        JsonObject? result = await RetryPolicies.DefaultPolicy.ExecuteAsync(
                            async (ctx) =>
                            {
                                this.logger.LogInformation(
                                    $"Fetching client assertion from '{url}'");
                                HttpResponseMessage response =
                                await govClient.PostAsync(url, null);
                                await response.ValidateStatusCodeAsync(this.logger);
                                return await response.Content.ReadFromJsonAsync<JsonObject>();
                            },
                            new Context("oauth/token", this.retryContextData));

                        return result?["value"]?.ToString();
                    });

                this.logger.LogInformation($"Fetching access token with clientId '{clientId}'.");

                return await RetryPolicies.FederatedCredsPolicy.ExecuteAsync(
                async (ctx) =>
                {
                    var accessToken = await credential.GetTokenAsync(
                        new TokenRequestContext([scope]),
                        CancellationToken.None);
                    return accessToken.Token;
                },
                new Context("GetTokenAsync", this.retryContextData));
            }
        }
    }

    private async Task<JobInput> ConvertJob(
        string queryId,
        RunQueryInput? runInput = null)
    {
        JobInput inputJob = await this.GetSqlJobInput(queryId, runInput);
        this.logger.LogInformation(
            $"Job input for queryId '{queryId}': {JsonSerializer.Serialize(inputJob)}");

        List<DatasetInfo> updatedDatasets = [];
        foreach (var dataset in inputJob.Datasets)
        {
            updatedDatasets.Add(ConvertDatasetForFrontendJob(dataset));
        }

        DatasetInfo updatedDatasink = ConvertDatasetForFrontendJob(inputJob.Datasink);

        var frontendJobInput = inputJob with
        {
            Datasets = updatedDatasets,
            Datasink = updatedDatasink
        };

        this.logger.LogInformation(
            $"Frontend job input for queryId '{queryId}': " +
            $"{JsonSerializer.Serialize(frontendJobInput)}");

        return frontendJobInput;

        DatasetInfo ConvertDatasetForFrontendJob(DatasetInfo dataset)
        {
            return dataset.AccessPoint.Store.Type switch
            {
                ResourceType.Azure_BlobStorage => ConvertBlobStorageDataset(),
                ResourceType.Azure_BlobStorage_DataLakeGen2 => ConvertBlobStorageDataset(),
                ResourceType.Aws_S3 => ConvertAwsS3Dataset(),
                _ => throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "UnsupportedAccessPointStoreType",
                        message: $"Access point store type '{dataset.AccessPoint.Store.Type}' " +
                        $"is not supported for query execution.")),
            };

            DatasetInfo ConvertBlobStorageDataset()
            {
                if (dataset.AccessPoint.Protection.EncryptionSecrets == null)
                {
                    this.logger.LogInformation($"No setup for access required as there are no " +
                        $"encryption secrets for dataset {dataset.Name}");
                    return dataset;
                }

                this.logger.LogInformation($"Converting dataset {dataset.Name}.");
                var encSecrets = dataset.AccessPoint.Protection.EncryptionSecrets;
                var accessIdentity = dataset.AccessPoint.Protection.EncryptionSecretAccessIdentity!;
                this.ValidateDataset(encSecrets, accessIdentity);

                var providerConfig = encSecrets.Kek!.Secret.BackingResource.Provider.Configuration!;
                var config = JsonSerializer.Deserialize<JsonObject>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(providerConfig)))!;
                var maaEndpoint = config["authority"]?.ToString();
                if (string.IsNullOrEmpty(maaEndpoint))
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        new ODataError(
                            code: "KekProviderConfigurationAuthorityMissing",
                            message:
                            "KEK provider configuration MAA endoint/authority value is missing."));
                }

                var secretName = this.ToCgsSecretName(
                    queryId,
                    encSecrets.Dek.Secret.BackingResource);
                var secretId = this.ToCgsSecretId(secretName);

                var protection = dataset.AccessPoint.Protection with
                {
                    EncryptionSecretAccessIdentity = null,
                    EncryptionSecrets = dataset.AccessPoint.Protection.EncryptionSecrets with
                    {
                        Kek = null,
                        Dek = dataset.AccessPoint.Protection.EncryptionSecrets.Dek with
                        {
                            Secret = dataset.AccessPoint.Protection.EncryptionSecrets.Dek.Secret with
                            {
                                BackingResource = dataset.AccessPoint.Protection.EncryptionSecrets
                                .Dek.Secret.BackingResource with
                                {
                                    Type = ResourceType.Cgs,
                                    Name = secretId,
                                    Provider = new ServiceEndpoint(
                                        Configuration: string.Empty,
                                        Protocol: ProtocolType.Cgs_Secret,
                                        Url: string.Empty)
                                }
                            }
                        }
                    },
                    Configuration = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize(new JsonObject
                        {
                            ["KeyType"] = "DEK",
                            ["EncryptionMode"] = "CPK"
                        })))
                };

                return dataset with
                {
                    AccessPoint = dataset.AccessPoint with { Protection = protection }
                };
            }

            DatasetInfo ConvertAwsS3Dataset()
            {
                var providerConfig = dataset.AccessPoint.Store.Provider.Configuration;
                if (string.IsNullOrEmpty(providerConfig))
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        new ODataError(
                            code: "StoreConfigurationMissing",
                            message: $"Store configuration is missing for dataset {dataset.Name}."));
                }

                var config = JsonSerializer.Deserialize<JsonObject>(
                    Encoding.UTF8.GetString(Convert.FromBase64String(providerConfig)));
                var value = config?["secretId"]?.ToString();
                if (string.IsNullOrEmpty(value))
                {
                    throw new ApiException(
                        HttpStatusCode.BadRequest,
                        new ODataError(
                            code: "StoreConfigurationSecretIdMissing",
                            message:
                            $"Store configuration secretId value is missing for dataset " +
                            $"{dataset.Name}."));
                }

                // Only validations above, no conversion required.
                return dataset;
            }
        }
    }

    private async Task<JobInput> GetSqlJobInput(
        string queryId,
        RunQueryInput? runInput = null)
    {
        var queryDocument = await this.GetUserDocument<SparkApplicationSpecification>(queryId);
        var query = queryDocument.Data.Application;
        if (query.OutputDataset == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "DatasinkMissing",
                    message: $"datasink value is not specified."));
        }

        if (query.InputDataset == null ||
            query.InputDataset.Count == 0)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "DatasetMissing",
                    message: $"Atleast one dataset must be specified."));
        }

        List<DatasetInfo> datasets = [];
        foreach (var dataset in query.InputDataset)
        {
            var datasetDocument = await this.GetUserDocument<Dataset>(dataset.Specification);
            if (datasetDocument.Data.Policy.AllowedFields == null ||
            !datasetDocument.Data.Policy.AllowedFields.Any())
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "AllowedFieldsNotSpecified",
                        message: $"Dataset {datasetDocument.Data.Name} is missing allowed fields."));
            }

            DatasetInfo datasetInfo = new(
                ViewName: dataset.View,
                OwnerId: datasetDocument.ProposerId,
                Name: datasetDocument.Data.Name,
                Format: datasetDocument.Data.Schema.Format,
                Schema: datasetDocument.Data.Schema.Fields.ToDictionary(
                    k => k.Name,
                    v => new SchemaFieldType(v.Type)),
                AccessPoint: datasetDocument.Data.AccessPoint,
                AllowedFields: datasetDocument.Data.Policy.AllowedFields.ToList());

            datasets.Add(datasetInfo);
        }

        var datasinkDocument =
            await this.GetUserDocument<Dataset>(query.OutputDataset.Specification);
        if (datasinkDocument.Data.Policy.AllowedFields == null ||
            !datasinkDocument.Data.Policy.AllowedFields.Any())
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "AllowedFieldsNotSpecified",
                    message: $"Datasink {datasinkDocument.Data.Name} is missing allowed fields."));
        }

        DatasetInfo datasinkEntry = new(
            ViewName: datasinkDocument.Data.Name,
            OwnerId: datasinkDocument.ProposerId,
            Name: datasinkDocument.Data.Name,
            Format: datasinkDocument.Data.Schema.Format,
            Schema: datasinkDocument.Data.Schema.Fields.ToDictionary(
                k => k.Name,
                v => new SchemaFieldType(v.Type)),
            AccessPoint: datasinkDocument.Data.AccessPoint,
            AllowedFields: datasinkDocument.Data.Policy.AllowedFields.ToList());

        if (query is SparkSQLApplication sparkSqlApp)
        {
            // Decode and validate the query string
            var encodedQueryStr = sparkSqlApp.Query;
            if (!string.IsNullOrEmpty(encodedQueryStr))
            {
                Query? queryObj = null;
                try
                {
                    var decodedQueryStr = Encoding.UTF8.GetString(
                        Convert.FromBase64String(encodedQueryStr));
                    queryObj = JsonSerializer.Deserialize<Query>(
                        decodedQueryStr,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        })!;
                }
                finally
                {
                    if (queryObj == null)
                    {
                        throw new ApiException(
                            HttpStatusCode.BadRequest,
                            new ODataError(
                                code: "InvalidQueryFormat",
                                message: $"DocumentId {queryId} has invalid Query format."));
                    }
                }
            }
        }

        var govJobInput = await GetGovernanceJobInput();
        return
            new JobInput(
                queryDocument.ContractId,
                query.Query,
                datasets,
                datasinkEntry,
                govJobInput,
                runInput?.StartDate,
                runInput?.EndDate,
                runInput?.DryRun,
                runInput?.UseOptimizer);

        async Task<GovernanceJobInput> GetGovernanceJobInput()
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
            if (string.IsNullOrEmpty(serviceCert) && gc.ServiceCertDiscovery == null)
            {
                throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "ServiceCertNotSpecified",
                        message: "No service cert or cert discovery information " +
                        "was retrieved for the governance endpoint."));
            }

            if (gc.ServiceCertDiscovery == null)
            {
                serviceCertBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(serviceCert!));
            }

            return new GovernanceJobInput(
                gc.CcrgovEndpoint,
                serviceCertBase64,
                gc.ServiceCertDiscovery);
        }
    }

    private async Task SetupSparkPodsAccess(JobInput job)
    {
        List<Task> setupTasks = [];
        HashSet<string> subjects = [];
        HashSet<string> secretIds = [];
        JobPolicy jobPolicy = await this.GetSqlJobPolicy(job);
        List<DatasetInfo> datasets = [.. job.Datasets, job.Datasink];
        foreach (var dataset in datasets)
        {
            switch (dataset.AccessPoint.Store.Type)
            {
                case ResourceType.Azure_BlobStorage:
                case ResourceType.Azure_BlobStorage_DataLakeGen2:
                    if (dataset.AccessPoint.Protection.EncryptionSecrets == null)
                    {
                        this.logger.LogInformation(
                            $"No encryption secrets for the specified dataset {dataset.Name}.");
                    }
                    else
                    {
                        secretIds.Add(dataset.AccessPoint.Protection.EncryptionSecrets.Dek.Secret
                            .BackingResource.Name);
                    }

                    subjects.Add(string.Join("-", job.ContractId, dataset.OwnerId));
                    break;

                case ResourceType.Aws_S3:
                    var providerConfig = dataset.AccessPoint.Store.Provider.Configuration;
                    var config = JsonSerializer.Deserialize<JsonObject>(
                        Encoding.UTF8.GetString(Convert.FromBase64String(providerConfig)))!;
                    secretIds.Add(config["secretId"]!.ToString());
                    break;

                default:
                    throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "UnsupportedAccessPointStoreType",
                        message: $"Access point store type '{dataset.AccessPoint.Store.Type}' " +
                        $"is not supported for query execution."));
            }
        }

        foreach (var secretId in secretIds)
        {
            setupTasks.Add(this.SetSecretAccessPolicy(secretId, jobPolicy));
        }

        foreach (var subject in subjects)
        {
            setupTasks.Add(this.SetIdpTokenAccessPolicy(subject, jobPolicy));
        }

        setupTasks.Add(this.SetEventsEmissionPolicy(jobPolicy));

        await Task.WhenAll(setupTasks);
    }

    private async Task<JobPolicy> GetSqlJobPolicy(JobInput job)
    {
        var frontendClient = await this.frontendClientManager.GetClient();

        // TODO (HPrabh): Check the query documents runtime options
        // to see if telemetry is enabled and pass it in to submitSqlJob.
        using var response = await frontendClient.PostAsync(
            "analytics/generateSecurityPolicy",
            JsonContent.Create(new
            {
                Job = job,
                enableTelemetryCollection = true
            }));

        await response.ValidateStatusCodeAsync(this.logger);
        var jobPolicy = (await response.Content.ReadFromJsonAsync<JobPolicy>())!;
        return jobPolicy;
    }

    private void ValidateDataset(EncryptionSecrets encSecrets, Identity? accessIdentity)
    {
        if (accessIdentity == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "AccessIdentityMissing",
                    message: $"encryptionSecretAccessIdentity is null."));
        }

        if (encSecrets.Dek.Secret.BackingResource.Type != ResourceType.AzureKeyVault)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "UnexpectedSecretBackingResourceType",
                    message: $"dek expecting type as 'AzureKeyVault' but found " +
                    $"'{encSecrets.Dek.Secret.BackingResource.Type}'."));
        }

        if (encSecrets.Dek.Secret.BackingResource.Provider.Protocol !=
            ProtocolType.AzureKeyVault_Secret)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "UnexpectedSecretBackingResourceType",
                    message: $"dek expecting protocol as 'AzureKeyVault_Secret' but " +
                    $"found " +
                    $"'{encSecrets.Dek.Secret.BackingResource.Provider.Protocol}'."));
        }

        if (encSecrets.Kek == null)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "KekNotSet",
                    message: $"Expecting kek to be set for AKV secret."));
        }

        if (encSecrets.Kek.Secret.BackingResource.Type != ResourceType.AzureKeyVault)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "UnexpectedSecretBackingResourceType",
                    message: $"kek expecting type as 'AzureKeyVault' but found " +
                    $"'{encSecrets.Kek.Secret.BackingResource.Type}'."));
        }

        if (encSecrets.Kek.Secret.BackingResource.Provider.Protocol !=
            ProtocolType.AzureKeyVault_SecureKey)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "UnexpectedSecretBackingResourceType",
                    message: $"kek expecting protocol as 'AzureKeyVault_SecureKey' but " +
                    $"found " +
                    $"'{encSecrets.Kek.Secret.BackingResource.Provider.Protocol}'."));
        }

        var providerConfig = encSecrets.Kek.Secret.BackingResource.Provider.Configuration;
        if (string.IsNullOrEmpty(providerConfig))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "KekProviderConfigurationMissing",
                    message: $"KEK provider configuration is null."));
        }
    }

    private string ToCgsSecretName(string queryId, Resource resource)
    {
        var suffix = BitConverter.ToString(
            SHA256.HashData(Encoding.UTF8.GetBytes(resource.Provider.Url)))
            .Replace("-", string.Empty)
            .ToLower();
        return $"{queryId}_{resource.Name}_{suffix}";
    }

    private string ToCgsSecretId(string cgsSecretName)
    {
        return "cleanroom_" + cgsSecretName;
    }

    private string FrontendJobIdFormat(string input)
    {
        string result = "cl-spark-" + Regex.Replace(input.ToLower(), @"[^a-z0-9-]", "-");
        return result.Length > 63 ? result[..63] : result;
    }
}