// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Controllers;

public static class GovernanceUtilities
{
    public static async Task LogAuditEventAsync(
        this HttpClient govClient,
        string message,
        ILogger logger)
    {
        var data = new
        {
            source = "spark-analytics-agent",
            message
        };
        using var response = await govClient.PutAsJsonAsync("/events", data);
        await response.ValidateStatusCodeAsync(logger);
    }
}