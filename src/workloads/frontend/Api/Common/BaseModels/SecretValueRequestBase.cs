// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Api.Common.BaseModels;

/// <summary>
/// Base class for secret value request across API versions.
/// </summary>
public abstract class SecretValueRequestBase
{
    /// <summary>
    /// Gets or sets the secret configuration value.
    /// </summary>
    public required string SecretConfig { get; set; }
}
