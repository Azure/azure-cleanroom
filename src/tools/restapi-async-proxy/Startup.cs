// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using ArmClient;
using Controllers;
using MetaRpDatabase.Test;

namespace RestApiAsyncProxy;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry => false;

    public override void OnConfigureServices(IServiceCollection services)
    {
        string? ep = this.Configuration[SettingName.TargetEndpoint];
        if (string.IsNullOrEmpty(ep))
        {
            throw new ArgumentException($"{SettingName.TargetEndpoint} env variable must be set.");
        }

        var targetEndpoint = new HttpClient
        {
            BaseAddress = new Uri(ep),
            Timeout = TimeSpan.FromMinutes(60) // Set a large timeout as the calls are long running.
        };

        IAsyncOperationsManager metaRpClient = new InMemoryAsyncOperationsManager();
        services.AddSingleton(metaRpClient);
        services.AddSingleton(targetEndpoint);
        services.AddHostedService<LongRunningService>();
        services.AddSingleton<BackgroundWorkerQueue>();
    }
}
