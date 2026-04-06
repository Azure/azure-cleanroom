// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

internal static class AuthMode
{
    public const string AzureLogin = "AzureLogin";
    public const string MsLogin = "MsLogin";
    public const string LocalIdp = "LocalIdp";
    public const string FromAuthHeader = "FromAuthHeader";
}
