// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

#pragma warning disable SA1300 // Element should begin with upper-case letter
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataFormat
{
    /// <summary>
    /// Represents CSV.
    /// </summary>
    [JsonStringEnumMemberName("csv")]
    csv,

    /// <summary>
    /// Represents JSON.
    /// </summary>
    [JsonStringEnumMemberName("json")]
    json,

    /// <summary>
    /// Represents Parquet.
    /// </summary>
    [JsonStringEnumMemberName("parquet")]
    parquet,
}
#pragma warning restore SA1300 // Element should begin with upper-case letter
