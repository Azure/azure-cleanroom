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

        JobInput frontendJob = await this.ConvertJob(queryId, startDate: null, endDate: null);
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

        DateTimeOffset? startDate = runInput.StartDate;
        DateTimeOffset? endDate = runInput.EndDate;
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
                        message: $"startDate and endDate must be in date format {this.dateFormat} with no timestamp."));
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

        var frontendClient = await this.frontendClientManager.GetClient();
        string runId;
        if (!string.IsNullOrEmpty(runInput.RunId))
        {
            // Check if this run was already submitted.
            var checkJobId = this.FrontendJobIdFormat(runInput.RunId);
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

        await this.CheckQueryApproved(queryId);
        JobInput frontendJob = await this.ConvertJob(queryId, startDate, endDate);
        await this.TransferSecrets(queryId);
        await this.SetupSparkPodsAccess(frontendJob);

        // TODO (HPrabh): Check the query documents runtime options
        // to see if telemetry is enabled and pass it in to submitSqlJob.
        using var response = await frontendClient.PostAsync(
            "/analytics/submitSqlJob",
            JsonContent.Create(new
            {
                JobId = runInput.RunId,
                Job = frontendJob,
                enableTelemetryCollection = true
            }));
        await response.ValidateStatusCodeAsync(this.logger);
        var submissionResult = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        this.logger.LogInformation(
            $"Query '{queryId}' with run Id '{runId}' submitted: " +
            $"{JsonSerializer.Serialize(submissionResult)}.");

        var submittedJobId = submissionResult["id"]?.ToString();
        var expectedJobId = this.FrontendJobIdFormat(runId);
        if (submittedJobId != expectedJobId)
        {
            // Some assumptions have changed. Fix this.
            throw new Exception(
                $"Expecting jobId to be {expectedJobId} but submit job returned job " +
                $"id {submittedJobId}.");
        }

        return this.Ok(submissionResult);
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

    private async Task CheckQueryApproved(string queryId)
    {
        var queryDocument =
            await this.GetUserDocument<QueryDocument>(queryId);

        this.logger.LogInformation(
            $"Checking if query '{queryId}' is approved by the required dataset owners.");

        if (queryDocument.Data.Datasets == null ||
            queryDocument.Data.Datasets.Count == 0)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "DatasetMissing",
                    message: $"Atleast one dataset must be specified."));
        }

        List<UserDocument<Dataset>> datasets = [];
        foreach ((var _, var docId) in queryDocument.Data.Datasets)
        {
            var datasetDoc = await this.GetUserDocument<Dataset>(docId);
            datasets.Add(datasetDoc);
        }

        var approvedBy =
            queryDocument.FinalVotes.Where(
                x => x.Ballot == Ballot.Accepted).Select(x => x.ApproverId).ToList();
        var missingApprovals = datasets.Where(d => !approvedBy.Contains(d.ProposerId))
            .Select(d => d.Id).ToList();
        if (missingApprovals.Count > 0)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "QueryMissingApprovalsFromDatasetOwners",
                    message: $"Query '{queryId}' requires approvals from the owners of the following datasets: {string.Join(", ", missingApprovals)}"));
        }
    }

    private async Task TransferSecrets(string queryId)
    {
        JobInput inputJob = await this.GetSqlJobInput(queryId, startDate: null, endDate: null);
        List<Dataset> datasets = [.. inputJob.Datasets, inputJob.Datasink];
        List<Task> transferTasks = [];
        foreach (var dataset in datasets)
        {
            transferTasks.Add(TransferDatasetSecret(dataset));
        }

        await Task.WhenAll(transferTasks);

        async Task TransferDatasetSecret(Dataset dataset)
        {
            switch (dataset.AccessPoint.Store.Type)
            {
                case ResourceType.Azure_BlobStorage:
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
        DateTimeOffset? startDate,
        DateTimeOffset? endDate)
    {
        JobInput inputJob = await this.GetSqlJobInput(queryId, startDate, endDate);
        this.logger.LogInformation(
            $"Job input for queryId '{queryId}': {JsonSerializer.Serialize(inputJob)}");

        List<Dataset> updatedDatasets = [];
        foreach (var dataset in inputJob.Datasets)
        {
            updatedDatasets.Add(ConvertDatasetForFrontendJob(dataset));
        }

        var updatedDatasink = ConvertDatasetForFrontendJob(inputJob.Datasink);

        var frontendJobInput = inputJob with
        {
            Datasets = updatedDatasets,
            Datasink = updatedDatasink
        };

        this.logger.LogInformation(
            $"Frontend job input for queryId '{queryId}': " +
            $"{JsonSerializer.Serialize(frontendJobInput)}");

        return frontendJobInput;

        Dataset ConvertDatasetForFrontendJob(Dataset dataset)
        {
            return dataset.AccessPoint.Store.Type switch
            {
                ResourceType.Azure_BlobStorage => ConvertBlobStorageDataset(),
                ResourceType.Aws_S3 => ConvertAwsS3Dataset(),
                _ => throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        code: "UnsupportedAccessPointStoreType",
                        message: $"Access point store type '{dataset.AccessPoint.Store.Type}' " +
                        $"is not supported for query execution.")),
            };

            Dataset ConvertBlobStorageDataset()
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

            Dataset ConvertAwsS3Dataset()
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
        DateTimeOffset? startDate,
        DateTimeOffset? endDate)
    {
        var queryDocument = await this.GetUserDocument<QueryDocument>(queryId);
        var query = queryDocument.Data;
        if (string.IsNullOrEmpty(query.Datasink))
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "DatasinkMissing",
                    message: $"datasink value is not specified."));
        }

        if (query.Datasets == null ||
            query.Datasets.Count == 0)
        {
            throw new ApiException(
                HttpStatusCode.BadRequest,
                new ODataError(
                    code: "DatasetMissing",
                    message: $"Atleast one dataset must be specified."));
        }

        List<Dataset> datasets = [];
        foreach ((var key, var docId) in query.Datasets)
        {
            var datasetDocument = await this.GetUserDocument<Dataset>(docId);
            var dataset = datasetDocument.Data with
            {
                ViewName = key,
                OwnerId = datasetDocument.ProposerId
            };
            datasets.Add(dataset);
        }

        var datasinkDocument =
            await this.GetUserDocument<Dataset>(query.Datasink);
        var datasinkEntry =
            datasinkDocument.Data with
            {
                ViewName = datasinkDocument.Data.Name,
                OwnerId = datasinkDocument.ProposerId
            };

        var govJobInput = await GetGovernanceJobInput();
        return
            new JobInput(
                queryDocument.ContractId,
                query.Query,
                datasets,
                datasinkEntry,
                govJobInput,
                startDate,
                endDate);

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
        List<Dataset> datasets = [.. job.Datasets, job.Datasink];
        foreach (var dataset in datasets)
        {
            switch (dataset.AccessPoint.Store.Type)
            {
                case ResourceType.Azure_BlobStorage:
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

        var providerConfig = encSecrets.Kek!.Secret.BackingResource.Provider.Configuration;
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