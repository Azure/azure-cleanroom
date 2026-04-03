// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FrontendSvc.Models;

public class QueryRunHistory
{
    [JsonPropertyName("queryId")]
    public string QueryId { get; set; } = string.Empty;

    [JsonPropertyName("latestRun")]
    public JobRun? LatestRun { get; set; }

    [JsonPropertyName("runs")]
    public List<JobRun> Runs { get; set; } = new List<JobRun>();

    [JsonPropertyName("summary")]
    public JobRecordSummary? Summary { get; set; }
}

public class JobRunError
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

public class JobRunStats
{
    [JsonPropertyName("rowsRead")]
    public int RowsRead { get; set; }

    [JsonPropertyName("rowsWritten")]
    public int RowsWritten { get; set; }
}

public class JobRun
{
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = string.Empty;

    [JsonPropertyName("startTime")]
    public DateTime? StartTime { get; set; }

    [JsonPropertyName("endTime")]
    public DateTime? EndTime { get; set; }

    [JsonPropertyName("isSuccessful")]
    public bool IsSuccessful { get; set; }

    [JsonPropertyName("error")]
    public JobRunError? Error { get; set; }

    [JsonPropertyName("stats")]
    public JobRunStats? Stats { get; set; }

    public double DurationSeconds
    {
        get
        {
            if (this.StartTime.HasValue && this.EndTime.HasValue)
            {
                return (this.EndTime.Value - this.StartTime.Value).TotalSeconds;
            }

            return 0.0;
        }
    }
}

public class JobRecordSummary
{
    [JsonPropertyName("totalRuns")]
    public int TotalRuns { get; set; }

    [JsonPropertyName("successfulRuns")]
    public int SuccessfulRuns { get; set; }

    [JsonPropertyName("failedRuns")]
    public int FailedRuns { get; set; }

    [JsonPropertyName("totalRuntimeSeconds")]
    public double TotalRuntimeSeconds { get; set; }

    [JsonPropertyName("avgDurationSeconds")]
    public double AvgDurationSeconds { get; set; }

    [JsonPropertyName("totalRowsRead")]
    public int TotalRowsRead { get; set; }

    [JsonPropertyName("totalRowsWritten")]
    public int TotalRowsWritten { get; set; }
}