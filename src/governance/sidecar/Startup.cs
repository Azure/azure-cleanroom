// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Controllers;

namespace CcrGovernance;

/// <summary>
/// The startup class.
/// </summary>
internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry => true;

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddControllers(options =>
        {
            options.Filters.Add<GlobalActionFilter>();
        });
        services.AddSingleton<CcfClientManager>();
        services.AddSingleton<Routes>();
    }
}