// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AciLoadBalancer;
using CAciCcfProvider;
using VirtualCcfProvider;

namespace CcfProviderClient;

public class ProvidersRegistry(
    DockerNodeProvider dockerNodeProvider,
    CAciNodeProvider caciNodeProvider,
    DockerEnvoyLoadBalancerProvider dockerEnvoyLbProvider,
    AciEnvoyLoadBalancerProvider aciEnvoyLbProvider,
    DockerRecoveryServiceInstanceProvider dockerRsProvider,
    CAciRecoveryServiceInstanceProvider caciRsProvider,
    DockerConsortiumManagerInstanceProvider dockerCmProvider,
    CAciConsortiumManagerInstanceProvider caciCmProvider)
{
    public DockerNodeProvider DockerNodeProvider { get; } = dockerNodeProvider;

    public CAciNodeProvider CaciNodeProvider { get; } = caciNodeProvider;

    public DockerEnvoyLoadBalancerProvider DockerEnvoyLbProvider { get; } = dockerEnvoyLbProvider;

    public AciEnvoyLoadBalancerProvider AciEnvoyLbProvider { get; } = aciEnvoyLbProvider;

    public DockerRecoveryServiceInstanceProvider DockerRsProvider { get; } = dockerRsProvider;

    public CAciRecoveryServiceInstanceProvider CaciRsProvider { get; } = caciRsProvider;

    public DockerConsortiumManagerInstanceProvider DockerCmProvider { get; } = dockerCmProvider;

    public CAciConsortiumManagerInstanceProvider CaciCmProvider { get; } = caciCmProvider;
}