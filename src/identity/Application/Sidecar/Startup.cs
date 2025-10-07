// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Controllers;
using Identity.Configuration;
using Identity.CredentialManager;
using IdentitySidecar.Filters;
using Metrics;
using Microsoft.Azure.IdentitySidecar.Telemetry.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Utilities;

namespace IdentitySidecar;

/// <summary>
/// The startup class.
/// </summary>
internal class Startup : ApiStartup
{
    private readonly IMetricsEmitter metricsEmitter;

    public Startup(IConfiguration config)
        : base(
            config,
            Assembly.GetExecutingAssembly().GetName().Name!,
            (loggingBuilder) =>
            {
                loggingBuilder.AddOpenTelemetry(options =>
                {
                    options.SetResourceBuilder(
                        ResourceBuilder.CreateDefault().AddService("identity"));
                    options.AddExporters(config);
                });
            })
    {
        this.metricsEmitter = MetricsEmitterBuilder.CreateBuilder().Build(
            Constants.Metrics.ServiceMeterName,
            () => IdentityMetric.Enumerate());
    }

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            options.Filters.Add<GlobalActionFilter>();
        });
        services.AddSingleton(this.metricsEmitter);

        var identityConfig = this.Configuration.GetIdentityConfiguration()!;
        var diagnosticsConfig = this.Configuration.GetDiagnosticsConfiguration()!;
        this.Logger.LogInformation(
            $"Starting Identity Sidecar with Configuration:" +
            $"{identityConfig.SafeToString()}");

        this.Logger.LogInformation(
            $"Starting Identity Sidecar with Diagnostics Configuration:" +
            $"{diagnosticsConfig.SafeToString()}");

        var credManager = new CredentialManager(identityConfig, this.Logger);
        services.AddSingleton(credManager);

        services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("identity"))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter()
            .AddExporters(this.Configuration))
        .WithTracing(tracing => tracing
            .AddSource("Microsoft.Azure.CleanRoomSidecar.Identity")
            .AddHttpClientInstrumentation()
            .AddAspNetCoreInstrumentation()
            .SetSampler(new AlwaysOnSampler())
            .AddExporters(this.Configuration));
    }

    public override void OnConfigure(WebApplication app, IWebHostEnvironment env)
    {
        this.metricsEmitter.Log(IdentityMetric.RoleStart());
    }
}