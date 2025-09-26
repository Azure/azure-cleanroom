// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using CAciCleanRoomProvider;
using VirtualCleanRoomProvider;

namespace CleanRoomProviderClient;

public class ProvidersRegistry(
    VirtualClusterProvider virtualClusterProvider,
    CAciClusterProvider cAciClusterProvider)
{
    public VirtualClusterProvider VirtualClusterProvider { get; } = virtualClusterProvider;

    public CAciClusterProvider CAciClusterProvider { get; } = cAciClusterProvider;
}