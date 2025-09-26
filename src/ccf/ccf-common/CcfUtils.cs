// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;
using AttestationClient;

namespace CcfCommon;

public static class CcfUtils
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static SecurityPolicyCreationOption ToOptionOrDefault(string? input)
    {
        if (!string.IsNullOrEmpty(input))
        {
            return Enum.Parse<SecurityPolicyCreationOption>(
            input,
            ignoreCase: true);
        }

        return SecurityPolicyCreationOption.cached;
    }
}