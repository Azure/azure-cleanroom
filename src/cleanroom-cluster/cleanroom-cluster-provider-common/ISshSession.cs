// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Resources;

namespace CleanRoomProvider;

/// <summary>
/// Represents an SSH session that can execute commands on a remote host.
/// </summary>
public interface ISshSession : IAsyncDisposable
{
    Task RunCommandAsync(
        string user,
        string privateKeyPath,
        string command,
        string? stdinContent = null,
        bool skipOutputLogging = false);

    Task<string> RunCommandWithOutputAsync(
        string user,
        string privateKeyPath,
        string command);
}
