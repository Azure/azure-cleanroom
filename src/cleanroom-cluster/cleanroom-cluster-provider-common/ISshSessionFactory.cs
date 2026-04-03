// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Resources;

namespace CleanRoomProvider;

public interface ISshSessionFactory
{
    Task<ISshSession> CreateSessionAsync(
        ResourceGroupResource resourceGroupResource,
        VirtualMachineResource vm);
}
