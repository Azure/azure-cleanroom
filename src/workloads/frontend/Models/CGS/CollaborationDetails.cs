// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class CollaborationDetails
{
    public required string ConsortiumEndpoint { get; init; }

    public required string ConsortiumServiceCertificatePem { get; init; }

    public required string UserToken { get; init; }

    public required HttpClient CgsClient { get; init; }

    public required string AnalyticsWorkloadId { get; init; }
}