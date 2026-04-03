// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using AksCleanRoomProvider;
using VirtualCleanRoomProvider;

namespace CleanRoomProviderClient;

public class ProvidersRegistry(
    VirtualClusterProvider virtualClusterProvider,
    AksClusterProvider aksClusterProvider)
{
    public VirtualClusterProvider VirtualClusterProvider { get; } = virtualClusterProvider;

    public AksClusterProvider AksClusterProvider { get; } = aksClusterProvider;
}