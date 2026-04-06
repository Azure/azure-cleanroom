// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using FrontendSvc.Models;

namespace FrontendSvc.Publisher;

public class DatasetDocumentPublisher(
    CollaborationDetails governanceClient,
    ILogger logger)
    : BaseDocumentPublisher<PublishDatasetInputDetails>(
        governanceClient,
        true,
        logger)
{
    public override async Task Publish(
        string id,
        PublishDatasetInputDetails input,
        string contractId)
    {
        try
        {
            this.Logger.LogInformation(
                $"Gathering data to publish dataset with id {id}");

            var labels = this.GetLabels();
            var approvers = this.GetApprovers(input);
            var datasetSpecification = this.GetDatasetSpecification(id, input);
            var data = JsonSerializer.Serialize(
                datasetSpecification, this.JsonSerializerOptions);

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
            this.Logger.LogError(ex, $"Failed to publish dataset with id {id}.");
            throw;
        }
    }

    private Dictionary<string, string> GetLabels()
    {
        return new Dictionary<string, string>
        {
            { "type", "dataset" },
        };
    }

    private List<UserProposalApprover> GetApprovers(PublishDatasetInputDetails input)
    {
        return
        [
            new()
            {
                ApproverId = input.CollaboratorId,
                ApproverIdType = "user",
            }
        ];
    }

    private DatasetSpecification GetDatasetSpecification(
        string id,
        PublishDatasetInputDetails input)
    {
        return new DatasetSpecification
        {
            Name = id,
            DatasetSchema = input.Data.DatasetSchema,
            DatasetAccessPolicy = input.Data.DatasetAccessPolicy,
            DatasetAccessPoint = input.Data.DatasetAccessPoint,
        };
    }
}
