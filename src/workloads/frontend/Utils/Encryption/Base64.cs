// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;

namespace FrontendSvc.Utils.Encryption;

public static class Base64
{
    public static string Encode(string input)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(input));
    }

    public static string Decode(string input)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(input));
    }
}
