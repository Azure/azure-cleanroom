// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using AciLoadBalancer;
using CAciCcfProvider;
using CcfProvider;
using Controllers;
using VirtualCcfProvider;

namespace CcfProviderClient;

internal class Startup : ApiStartup
{
    public Startup(IConfiguration config)
        : base(config, Assembly.GetExecutingAssembly().GetName().Name!)
    {
    }

    public override void OnConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<CcfClientManager>();
        services.AddSingleton<RecoveryAgentClientManager>();
        services.AddSingleton<RecoveryServiceClientManager>();

        // Add node providers
        services.AddSingleton<DockerNodeProvider>();
        services.AddSingleton<CAciNodeProvider>();

        // Add LB providers
        services.AddSingleton<DockerEnvoyLoadBalancerProvider>();
        services.AddSingleton<AciEnvoyLoadBalancerProvider>();

        // Add recovery service providers
        services.AddSingleton<DockerRecoveryServiceInstanceProvider>();
        services.AddSingleton<CAciRecoveryServiceInstanceProvider>();

        // Add consortium manager providers
        services.AddSingleton<DockerConsortiumManagerInstanceProvider>();
        services.AddSingleton<CAciConsortiumManagerInstanceProvider>();

        services.AddSingleton<ProvidersRegistry>();

        services.AddSingleton<BackgroundTaskQueue>();
        services.AddSingleton<IOperationStore, InMemoryOperationStore>();
        services.AddHostedService<BackgroundWorker>();
    }
}
