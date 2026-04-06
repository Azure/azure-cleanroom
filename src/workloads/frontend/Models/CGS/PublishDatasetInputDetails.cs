// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Models;

public class PublishDatasetInputDetails : BaseDataPublishInputDetails
{
    public required DatasetSpecification Data { get; set; }

    public string CollaboratorId { get; set; } = string.Empty;
}