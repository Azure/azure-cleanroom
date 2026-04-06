// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class CollaborationOutput
{
    public required string CollaborationId { get; set; }

    public required string CollaborationName { get; set; }

    public required string UserStatus { get; set; }
}