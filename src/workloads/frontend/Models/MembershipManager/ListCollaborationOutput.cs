// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class ListCollaborationsOutput
{
    public required List<CollaborationOutput> Collaborations { get; set; }
}