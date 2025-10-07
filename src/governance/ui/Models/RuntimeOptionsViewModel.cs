// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CgsUI.Models;

public class RuntimeOptionsViewModel
{
    public string Id { get; set; } = default!;

    public LoggingOptionViewModel Logging { get; set; } = default!;

    public TelemetryOptionViewModel Telemetry { get; set; } = default!;

    public ExecutionOptionViewModel Execution { get; set; } = default!;
}

public class LoggingOptionViewModel
{
    public string[] ProposalIds { get; set; } = default!;

    public string Status { get; set; } = default!;
}

public class TelemetryOptionViewModel
{
    public string[] ProposalIds { get; set; } = default!;

    public string Status { get; set; } = default!;

    public ReasonModel Reason { get; set; } = default!;
}

public class ExecutionOptionViewModel
{
    public string Status { get; set; } = default!;

    public ReasonModel Reason { get; set; } = default!;
}

public class ReasonModel
{
    public string Code { get; set; } = default!;

    public string Message { get; set; } = default!;
}