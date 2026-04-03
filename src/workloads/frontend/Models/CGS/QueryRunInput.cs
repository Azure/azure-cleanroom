// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class QueryRunInput
{
    public required string RunId { get; set; }

    public string? StartDate { get; set; }

    public string? EndDate { get; set; }

    public bool UseOptimizer { get; set; } = false;

    public bool DryRun { get; set; } = false;
}