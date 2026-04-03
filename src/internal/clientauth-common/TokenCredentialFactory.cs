// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

namespace TokenCredentials;

public static class TokenCredentialFactory
{
    public static TokenCredential GetAzureCredential()
    {
        return new DefaultAzureCredential();
    }

    public static TokenCredential GetTenantCredential(JsonObject? providerConfig)
    {
        string? tenantId = providerConfig?["tenantId"]?.ToString();
        if (string.IsNullOrEmpty(tenantId))
        {
            throw new ArgumentException("TenantId is null.");
        }

        if (IsTenantIdentityEndpointConfigured())
        {
            return new TenantTokenCredential(tenantId);
        }

        // DefaultAzureCredential will pick up the creds of the currently logged-in user. If
        // this user does not have access to the provided TenantId, an Unauthorized will be
        // hit on actual creds usage.
        return new DefaultAzureCredential();
    }

    private static bool IsTenantIdentityEndpointConfigured()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TENANT_IDENTITY_ENDPOINT"));
    }
}
