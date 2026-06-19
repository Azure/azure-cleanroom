// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Controllers;

namespace CgsClient;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry =>
        this.Configuration.GetValue<bool>("CGS_CLIENT_ENABLE_OPEN_TELEMETRY");

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
    }
}