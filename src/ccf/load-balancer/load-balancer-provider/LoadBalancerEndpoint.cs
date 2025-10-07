// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace LoadBalancerProvider;

public class LoadBalancerEndpoint
{
    public string Name { get; set; } = default!;

    public string Endpoint { get; set; } = default!;

    public string AgentEndpoint { get; set; } = default!;
}