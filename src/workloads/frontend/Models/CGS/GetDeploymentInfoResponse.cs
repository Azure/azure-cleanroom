// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class GetDeploymentInfoResponse
{
    public List<string> ProposalIds { get; set; } = new();

    public object? Data { get; set; }
}