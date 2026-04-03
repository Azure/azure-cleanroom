// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json.Serialization;
using Controllers;

namespace FrontendSvc.Models;

public class PreCondition
{
    [JsonPropertyName("viewName")]
    public required string ViewName { get; set; }

    [JsonPropertyName("minRowCount")]
    public required int MinRowCount { get; set; }

    public static PreCondition FromString(string preConditionString)
    {
        var parts = preConditionString.Split(':');
        if (parts.Length != 2)
        {
            throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        "InvalidPreConditionFormat",
                        "PreCondition is expected to be a ':' separated string of the format " +
                        "<ViewName>:<MinRowCount>."));
        }

        if (!int.TryParse(parts[1].Trim(), out var minRowCount) ||
            minRowCount < 0)
        {
            throw new ApiException(
                    HttpStatusCode.BadRequest,
                    new ODataError(
                        "InvalidPreConditionFormat",
                        "MinRowCount in PreCondition must be a valid integer."));
        }

        return new PreCondition
        {
            ViewName = parts[0].Trim(),
            MinRowCount = minRowCount,
        };
    }
}
