// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FrontendSvc.Models;
using FrontendSvc.Utils.Encryption;

namespace FrontendSvc.Publisher;

public class QueryDocumentPublisher(
    CollaborationDetails governanceClient,
    ILogger logger)
    : BaseDocumentPublisher<PublishQueryInputDetails>(
        governanceClient,
        false,
        logger)
{
    public override async Task Publish(string id, PublishQueryInputDetails input, string contractId)
    {
        try
        {
            this.Logger.LogInformation(
                $"Gathering data to publish query with id {id}");

            var labels = this.GetLabels();
            var approvers = await this.GetApprovers(input);

            var sparkApplicationSpecification = this.GetSparkApplicationSpecification(id, input);
            var data = JsonSerializer.Serialize(sparkApplicationSpecification);

            this.Logger.LogInformation(
                $"Publishing user document with id {id} for contract {contractId}");

            var userDocument = new CreateUserDocument
            {
                ContractId = contractId,
                Labels = labels,
                Approvers = approvers,
                Data = data,
            };

            await this.PublishDocument(
                id,
                userDocument);
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, $"Failed to publish query with id {id}.");
            throw;
        }
    }

    private Dictionary<string, string> GetLabels()
    {
        return new Dictionary<string, string>
        {
            { "type", "spark-application" },
        };
    }

    private async Task<List<UserProposalApprover>> GetApprovers(
        PublishQueryInputDetails queryInputDetails)
    {
        // Approvers are fetched from the governance endpoint and not from the input
        // This is to ensure that the approvers are not spoofed by the customer sending the request
        var approverIds = new HashSet<string>();

        foreach (var inputDataset in queryInputDetails.InputDatasets)
        {
            approverIds.Add(await this.GetApprover(inputDataset.DatasetDocumentId));
        }

        approverIds.Add(await this.GetApprover(queryInputDetails.OutputDataset.DatasetDocumentId));

        this.Logger.LogInformation(
            $"Identified {approverIds.Count} unique approvers for the query.");

        return [.. approverIds.Select(
            id => new UserProposalApprover { ApproverId = id, ApproverIdType = "user" })];
    }

    private async Task<string> GetApprover(string documentId)
    {
        var userDocument = await this.GetUserDocument(documentId);
        return userDocument.ProposerId;
    }

    private SparkApplicationSpecification GetSparkApplicationSpecification(
        string id, PublishQueryInputDetails queryInputDetails)
    {
        return new SparkApplicationSpecification
        {
            Name = id,
            Application = this.GetSparkSQLApplication(queryInputDetails),
        };
    }

    private SparkSQLApplication GetSparkSQLApplication(PublishQueryInputDetails queryInputDetails)
    {
        var encodedQuery = this.GetEncodedQuery(queryInputDetails.QueryData);
        var inputDatasetDescriptors = queryInputDetails.InputDatasets
            .Select(this.GetSparkApplicationDatasetDescriptor)
            .ToList();
        var outputDatasetDescriptor = this.GetSparkApplicationDatasetDescriptor(
            queryInputDetails.OutputDataset);

        return new SparkSQLApplication
        {
            ApplicationType = SparkApplicationType.SparkSQL,
            InputDataset = inputDatasetDescriptors,
            OutputDataset = outputDatasetDescriptor,
            Query = encodedQuery,
        };
    }

    private string GetEncodedQuery(Query query)
    {
        var queryJsonString = JsonSerializer.Serialize(query);
        var encodedQuery = Base64.Encode(queryJsonString);

        this.Logger.LogInformation(
            $"Encoded query string length: {encodedQuery.Length}");

        return encodedQuery;
    }

    private SparkApplicationDatasetDescriptor GetSparkApplicationDatasetDescriptor(
        QueryDatasetInput datasetInput)
    {
        return new SparkApplicationDatasetDescriptor
        {
            Specification = datasetInput.DatasetDocumentId,
            View = datasetInput.View,
        };
    }
}
