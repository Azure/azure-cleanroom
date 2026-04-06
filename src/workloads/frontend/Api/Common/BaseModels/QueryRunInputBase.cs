// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Api.Common.BaseModels;

/// <summary>
/// Base class for query run input across API versions.
/// </summary>
public abstract class QueryRunInputBase
{
    /// <summary>
    /// Gets or sets the unique identifier for this run.
    /// </summary>
    public required string RunId { get; set; }

    /// <summary>
    /// Gets or sets the optional start date filter for the query.
    /// </summary>
    public string? StartDate { get; set; }

    /// <summary>
    /// Gets or sets the optional end date filter for the query.
    /// </summary>
    public string? EndDate { get; set; }
}
