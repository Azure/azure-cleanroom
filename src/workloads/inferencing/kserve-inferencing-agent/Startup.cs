// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Controllers;

namespace KServeInferencingAgent;

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
        services.AddSingleton(new FrontendClientConfig(
            SettingName.InferencingFrontendEndpoint,
            SettingName.InferencingFrontendSnpHostData,
            "inferencing-frontend"));
        services.AddSingleton<FrontendClientManager>();
        services.AddSingleton<GovernanceClientManager>();
        services.AddSingleton<ActiveUserChecker>();
    }
}