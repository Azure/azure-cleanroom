// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using FrontendSvc.Api.Common.BaseModels;

namespace FrontendSvc.Api.V2026_03_01_Preview.Models;

/// <summary>
/// Input for running a query in API version 2026-03-01-preview.
/// </summary>
public class QueryRunInput : QueryRunInputBase
{
    /// <summary>
    /// Gets or sets a value indicating whether to use the AI optimizer for Spark configuration.
    /// </summary>
    public bool UseOptimizer { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether to perform a dry run
    /// (returns SKU settings without execution).
    /// </summary>
    public bool DryRun { get; set; } = false;
}
