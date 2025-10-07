// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Controllers;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CleanRoomAnalyticsAgent;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(
            config,
            Assembly.GetExecutingAssembly().GetName().Name!,
            (loggingBuilder) =>
            {
                loggingBuilder.AddOpenTelemetry(options =>
                {
                    options.SetResourceBuilder(ResourceBuilder.CreateDefault()
                        .AddService("cleanroom-spark-analytics-agent"));
                    options.AddOtlpExporter();
                    options.AddConsoleExporter();
                });
            })
    {
    }

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SparkFrontendClientManager>();
        services.AddSingleton<GovernanceClientManager>();
        services.AddSingleton<ActiveUserChecker>();

        // OpenTelemetry configuration
        services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .SetResourceBuilder(
                        ResourceBuilder.CreateDefault()
                            .AddService("cleanroom-spark-analytics-agent"))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(
                        ResourceBuilder.CreateDefault()
                            .AddService("cleanroom-spark-analytics-agent"))
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter();
            });
    }
}