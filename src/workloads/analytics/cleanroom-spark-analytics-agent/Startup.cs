// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Controllers;

namespace CleanRoomAnalyticsAgent;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(
            config,
            Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry => true;

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<SparkFrontendClientManager>();
        services.AddSingleton<GovernanceClientManager>();
        services.AddSingleton<ActiveUserChecker>();
    }
}