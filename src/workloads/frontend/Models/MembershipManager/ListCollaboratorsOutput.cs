// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Models;

public class ListCollaboratorsOutput
{
    public required List<CollaboratorOutput> Collaborators { get; set; }
}
