// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace FrontendSvc.Api.Common.BaseModels;

/// <summary>
/// Base class for consent action request across API versions.
/// </summary>
public abstract class ConsentActionRequestBase
{
    /// <summary>
    /// Gets or sets the consent action to perform (enable/disable).
    /// </summary>
    public string ConsentAction { get; set; } = default!;
}
