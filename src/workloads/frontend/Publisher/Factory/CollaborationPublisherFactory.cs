// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FrontendSvc.Models;

namespace FrontendSvc.Publisher.Factory;

public class CollaborationPublisherFactory(ILogger logger) : ICollaborationPublisherFactory
{
    private readonly ILogger logger = logger;

    public ICollaborationPublisher<PublishDatasetInputDetails> GetDatasetDocumentPublisher(
        CollaborationDetails governanceClient)
    {
        return new DatasetDocumentPublisher(governanceClient, this.logger);
    }

    public ICollaborationPublisher<PublishQueryInputDetails> GetQueryDocumentPublisher(
        CollaborationDetails governanceClient)
    {
        return new QueryDocumentPublisher(governanceClient, this.logger);
    }
}
