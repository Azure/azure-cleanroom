// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using AksCleanRoomProvider;
using Controllers;
using VirtualCleanRoomProvider;

namespace CleanRoomProviderClient;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override bool EnableOpenTelemetry => false;

    public override void OnConfigureServices(IServiceCollection services)
    {
        // Add cluster providers
        services.AddSingleton<VirtualClusterProvider>();
        services.AddSingleton<AksClusterProvider>();

        services.AddSingleton<ProvidersRegistry>();

        services.AddSingleton<BackgroundTaskQueue>();
        services.AddSingleton<IOperationStore, InMemoryOperationStore>();
        services.AddHostedService<BackgroundWorker>();
    }
}
