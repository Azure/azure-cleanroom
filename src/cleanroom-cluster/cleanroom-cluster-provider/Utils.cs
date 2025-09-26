// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanRoomProvider;

public static class Utils
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

    public static string GetUniqueString(string id, int length = 13)
    {
        using (var hash = SHA512.Create())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(id);
            var hashedInputBytes = hash.ComputeHash(bytes);
            List<char> a = new();
            for (int i = 1; i <= length; i++)
            {
                var b = hashedInputBytes[i];
                var x = (char)((b % 26) + (byte)'a');
                a.Add(x);
            }

            return new string(a.ToArray());
        }
    }
}
