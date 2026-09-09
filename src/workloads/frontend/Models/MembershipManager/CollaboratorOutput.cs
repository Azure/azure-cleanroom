// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Models;

public class CollaboratorOutput
{
    public required string UserIdentifier { get; set; }

    public required bool IsOwner { get; set; }

    public string? ObjectId { get; set; }

    public string? TenantId { get; set; }
}
