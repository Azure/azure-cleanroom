// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

#pragma warning disable SA1300 // Element should begin with upper-case letter
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataFieldType
{
    /// <summary>
    /// Represents string.
    /// </summary>
    [JsonStringEnumMemberName("string")]
    String,

    /// <summary>
    /// Represents integer.
    /// </summary>
    [JsonStringEnumMemberName("integer")]
    Integer,

    /// <summary>
    /// Represents number.
    /// </summary>
    [JsonStringEnumMemberName("number")]
    Number,

    /// <summary>
    /// Represents boolean.
    /// </summary>
    [JsonStringEnumMemberName("boolean")]
    Boolean,

    /// <summary>
    /// Represents date.
    /// </summary>
    [JsonStringEnumMemberName("date")]
    Date,

    /// <summary>
    /// Represents long.
    /// </summary>
    [JsonStringEnumMemberName("long")]
    Long,

    /// <summary>
    /// Represents timestamp.
    /// </summary>
    [JsonStringEnumMemberName("timestamp")]
    Timestamp,
}
#pragma warning restore SA1300 // Element should begin with upper-case letter
