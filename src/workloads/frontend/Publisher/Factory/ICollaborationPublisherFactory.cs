// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FrontendSvc.Models;

namespace FrontendSvc.Publisher.Factory;

public interface ICollaborationPublisherFactory
{
    ICollaborationPublisher<PublishQueryInputDetails> GetQueryDocumentPublisher(
        CollaborationDetails governanceClient);

    ICollaborationPublisher<PublishDatasetInputDetails> GetDatasetDocumentPublisher(
        CollaborationDetails governanceClient);
}
