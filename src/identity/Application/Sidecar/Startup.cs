// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Controllers;
using Identity.Configuration;
using Identity.CredentialManager;
using IdentitySidecar.Filters;
using Metrics;
using Microsoft.Azure.IdentitySidecar.Telemetry.Metrics;
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
            Assembly.GetExecutingAssembly().GetName().Name!)
    {
        this.metricsEmitter = MetricsEmitterBuilder.CreateBuilder().Build(
            Constants.Metrics.ServiceMeterName,
            () => IdentityMetric.Enumerate());
    }

    public override bool EnableOpenTelemetry => true;

    public override string? OTelServiceName => "identity";

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            options.Filters.Add<GlobalActionFilter>();
        });
        services.AddSingleton(this.metricsEmitter);

        var identityConfig = this.Configuration.GetIdentityConfiguration();
        this.Logger.LogInformation(
            $"Starting Identity Sidecar with Configuration:" +
            $"{identityConfig.SafeToString()}");

        var credManager = new CredentialManager(identityConfig, this.Logger);
        services.AddSingleton(credManager);
    }

    public override void OnConfigure(WebApplication app, IWebHostEnvironment env)
    {
        this.metricsEmitter.Log(IdentityMetric.RoleStart());
    }
}