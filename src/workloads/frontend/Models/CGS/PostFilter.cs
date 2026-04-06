// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Serialization;
using Controllers;

namespace FrontendSvc.Models;

public class PostFilter
{
    [JsonPropertyName("columnName")]
    public required string ColumnName { get; set; }

    [JsonPropertyName("value")]
    public required int Value { get; set; }

    public static PostFilter FromString(string postFilterString)
    {
        var parts = postFilterString.Split(':');
        if (parts.Length != 2)
        {
            throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        "InvalidPostFilterFormat",
                        "PostFilter is expected to be a ':' separated string of the format " +
                        "<ColumnName>:<Value>."));
        }

        if (!int.TryParse(parts[1].Trim(), out var value) ||
            value < 0)
        {
            throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        "InvalidPostFilterFormat",
                        "Value in PostFilter must be a valid integer."));
        }

        return new PostFilter
        {
            ColumnName = parts[0].Trim(),
            Value = value,
        };
    }
}
