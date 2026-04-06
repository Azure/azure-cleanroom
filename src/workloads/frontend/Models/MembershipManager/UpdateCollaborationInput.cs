// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class UpdateCollaboratorStatusInput
{
    public required string UserStatus { get; set; }
}
