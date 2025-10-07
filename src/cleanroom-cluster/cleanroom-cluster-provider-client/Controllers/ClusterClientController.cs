// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CleanRoomProvider;
using CleanRoomProviderClient;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class ClusterClientController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly ProvidersRegistry providers;

    public ClusterClientController(
        ILogger logger,
        IConfiguration configuration,
        ProvidersRegistry providers)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.providers = providers;
    }

    protected static IProgress<string> NoOpProgressReporter => new Progress<string>(m => { });

    protected ClusterProvider GetCleanRoomClusterProvider(InfraType infraType)
    {
        ICleanRoomClusterProvider provider = infraType switch
        {
            InfraType.@virtual => this.providers.VirtualClusterProvider,
            InfraType.caci => this.providers.CAciClusterProvider,
            _ => throw new NotSupportedException($"Infra type '{infraType}' is not supported."),
        };

        var clusterProvider = new ClusterProvider(this.logger, provider);
        return clusterProvider;
    }
}
