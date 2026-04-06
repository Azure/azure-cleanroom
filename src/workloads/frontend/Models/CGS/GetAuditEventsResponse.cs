// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class AuditEventData
{
    public string? Source { get; set; } = string.Empty;

    public string? Message { get; set; } = string.Empty;
}

public class AuditEvent
{
    public string Scope { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;

    public string Timestamp { get; set; } = string.Empty;

    public string TimestampIso { get; set; } = string.Empty;

    public AuditEventData? Data { get; set; }
}

public class GetAuditEventsResponse
{
    public List<AuditEvent> Value { get; set; } = new();

    public string? NextLink { get; set; }
}