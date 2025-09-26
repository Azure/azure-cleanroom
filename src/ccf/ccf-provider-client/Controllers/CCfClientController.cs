// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CcfConsortiumMgrProvider;
using CcfProvider;
using CcfProviderClient;
using CcfRecoveryProvider;
using LoadBalancerProvider;
using Microsoft.AspNetCore.Mvc;

namespace Controllers;

public abstract class CCfClientController : ControllerBase
{
    private readonly ILogger logger;
    private readonly IConfiguration configuration;
    private readonly ProvidersRegistry providers;

    public CCfClientController(
        ILogger logger,
        IConfiguration configuration,
        ProvidersRegistry providers)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.providers = providers;
    }

    protected static IProgress<string> NoOpProgressReporter => new Progress<string>(m => { });

    protected ICcfNodeProvider GetNodeProvider(InfraType infraType)
    {
        return infraType switch
        {
            InfraType.@virtual => this.providers.DockerNodeProvider,
            InfraType.caci => this.providers.CaciNodeProvider,
            _ => throw new NotSupportedException($"Infra type '{infraType}' is not supported."),
        };
    }

    protected ICcfLoadBalancerProvider GetLoadBalancerProvider(InfraType infraType)
    {
        return infraType switch
        {
            InfraType.@virtual => this.providers.DockerEnvoyLbProvider,
            InfraType.caci => this.providers.AciEnvoyLbProvider,
            _ => throw new NotSupportedException($"Infra type '{infraType}' is not supported."),
        };
    }

    protected ICcfRecoveryServiceInstanceProvider GetRecoverySvcInstanceProvider(
        RsInfraType infraType)
    {
        return infraType switch
        {
            RsInfraType.@virtual => this.providers.DockerRsProvider,
            RsInfraType.caci => this.providers.CaciRsProvider,
            _ => throw new NotSupportedException($"Infra type '{infraType}' is not supported."),
        };
    }

    protected ICcfConsortiumManagerInstanceProvider GetConsortiumMgrInstanceProvider(
        CMInfraType infraType)
    {
        return infraType switch
        {
            CMInfraType.@virtual => this.providers.DockerCmProvider,
            CMInfraType.caci => this.providers.CaciCmProvider,
            _ => throw new NotSupportedException($"Infra type '{infraType}' is not supported."),
        };
    }

    protected CcfRecoveryServiceProvider GetRecoveryServiceProvider(
        string infraType)
    {
        var type = Enum.Parse<RsInfraType>(infraType, ignoreCase: true);
        ICcfRecoveryServiceInstanceProvider provider = this.GetRecoverySvcInstanceProvider(type);
        var ccfRecoverySvcProvider = new CcfRecoveryServiceProvider(
            this.logger,
            provider);
        return ccfRecoverySvcProvider;
    }

    protected CcfConsortiumManagerProvider GetConsortiumManagerProvider(string infraType)
    {
        var type = Enum.Parse<CMInfraType>(infraType, ignoreCase: true);
        ICcfConsortiumManagerInstanceProvider instanceProvider =
            this.GetConsortiumMgrInstanceProvider(type);

        return new CcfConsortiumManagerProvider(this.logger, instanceProvider);
    }
}
