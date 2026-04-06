// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Api.Common.BaseModels;

/// <summary>
/// Base class for set issuer URL input across API versions.
/// </summary>
public abstract class SetIssuerUrlInputBase
{
    /// <summary>
    /// Gets or sets the OIDC issuer URL to set.
    /// </summary>
    public required string Url { get; set; }
}
