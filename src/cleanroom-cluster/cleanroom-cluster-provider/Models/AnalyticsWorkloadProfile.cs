// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CleanRoomProvider;

public class AnalyticsWorkloadProfile
{
    public bool Enabled { get; set; }

    public string? Namespace { get; set; }

    public string? Endpoint { get; set; }

    // TODO (gsinha): Populate these by saving the inputs as annotations on the namesace.
    public string? ConfigurationUrl { get; set; }

    public string? ConfigurationUrlCaCert { get; set; }
}
