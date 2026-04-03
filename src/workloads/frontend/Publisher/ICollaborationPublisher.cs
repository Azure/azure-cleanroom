// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FrontendSvc.Models;

namespace FrontendSvc.Publisher;

public interface ICollaborationPublisher<T>
    where T : BaseDataPublishInputDetails
{
    Task Publish(string id, T input, string contractId);
}
