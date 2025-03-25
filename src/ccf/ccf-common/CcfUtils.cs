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

    public static bool IsSevSnp()
    {
        return !IsVirtualEnvironment();
    }

    public static bool IsVirtualEnvironment()
    {
        return Environment.GetEnvironmentVariable("INSECURE_VIRTUAL_ENVIRONMENT") == "true";
    }

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

    public static async Task<string> GetHostData()
    {
        if (IsSevSnp())
        {
            var hostData = await Attestation.GetHostDataAsync();
            return hostData;
        }
        else
        {
            return "73973b78d70cc68353426de188db5dfc57e5b766e399935fb73a61127ea26d20";
        }
    }
}