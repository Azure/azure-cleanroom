// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsentAction
{
    /// <summary>
    /// Enable execution consent for the document.
    /// </summary>
    Enable,

    /// <summary>
    /// Disable execution consent for the document.
    /// </summary>
    Disable
}