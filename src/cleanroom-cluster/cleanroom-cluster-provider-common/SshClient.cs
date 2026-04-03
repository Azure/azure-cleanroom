// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Sockets;
using System.Text;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using Common;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

/// <summary>
/// Factory for creating direct SSH sessions (without proxy).
/// Uses the VM's public IP address for direct connectivity.
/// </summary>
public class DirectSshSessionFactory : ISshSessionFactory
{
    private readonly ILogger logger;

    public DirectSshSessionFactory(ILogger logger)
    {
        this.logger = logger;
    }

    public async Task<ISshSession> CreateSessionAsync(
        ResourceGroupResource resourceGroupResource,
        VirtualMachineResource vm)
    {
        string publicIpAddress = await this.GetVmPublicIpAddressAsync(resourceGroupResource, vm);
        this.logger.LogInformation($"Using public IP address for direct SSH: {publicIpAddress}");

        await this.WaitForSshConnectivityAsync(publicIpAddress);
        return new DirectSshSession(this.logger, publicIpAddress);
    }

    private async Task<string> GetVmPublicIpAddressAsync(
        ResourceGroupResource resourceGroupResource,
        VirtualMachineResource vm)
    {
        var nicReference = vm.Data.NetworkProfile.NetworkInterfaces.FirstOrDefault();
        if (nicReference == null)
        {
            throw new InvalidOperationException(
                $"VM {vm.Data.Name} does not have any network interfaces.");
        }

        var nicName = nicReference.Id.Name;
        var nic = await resourceGroupResource.GetNetworkInterfaceAsync(nicName);

        var ipConfig = nic.Value.Data.IPConfigurations.FirstOrDefault();
        if (ipConfig?.PublicIPAddress == null)
        {
            throw new InvalidOperationException(
                $"NIC {nicName} does not have a public IP address.");
        }

        var publicIpName = ipConfig.PublicIPAddress.Id.Name;
        var publicIp = await resourceGroupResource.GetPublicIPAddressAsync(publicIpName);

        if (string.IsNullOrEmpty(publicIp.Value.Data.IPAddress))
        {
            throw new InvalidOperationException(
                $"Public IP {publicIpName} does not have an assigned IP address.");
        }

        return publicIp.Value.Data.IPAddress;
    }

    private async Task WaitForSshConnectivityAsync(string ipAddress)
    {
        int port = 22;
        var maxWait = TimeSpan.FromMinutes(10);
        var retryInterval = TimeSpan.FromSeconds(15);
        var elapsed = TimeSpan.Zero;

        this.logger.LogInformation(
            $"Waiting for SSH connectivity to {ipAddress}:{port} " +
            $"(timeout: {maxWait.TotalMinutes} minutes)...");

        while (elapsed < maxWait)
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ipAddress, port);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10));

                if (await Task.WhenAny(connectTask, timeoutTask) == connectTask && client.Connected)
                {
                    this.logger.LogInformation(
                        $"SSH connectivity established to {ipAddress}:{port}.");
                    return;
                }
            }
            catch (SocketException)
            {
                // Connection failed, will retry.
            }

            this.logger.LogInformation(
                $"SSH not yet available at {ipAddress}:{port}, retrying in " +
                $"{retryInterval.TotalSeconds} seconds... ({elapsed.TotalSeconds}s elapsed)");
            await Task.Delay(retryInterval);
            elapsed += retryInterval;
        }

        throw new TimeoutException(
            $"SSH connectivity to {ipAddress}:{port} was not established within " +
            $"{maxWait.TotalMinutes} minutes.");
    }
}

/// <summary>
/// Represents a direct SSH session to a remote host (without proxy).
/// </summary>
public class DirectSshSession : RunCommand, ISshSession
{
    private readonly ILogger logger;
    private readonly string targetIpAddress;

    public DirectSshSession(ILogger logger, string targetIpAddress)
        : base(logger)
    {
        this.logger = logger;
        this.targetIpAddress = targetIpAddress;
    }

    /// <inheritdoc/>
    public async Task RunCommandAsync(
        string user,
        string privateKeyPath,
        string command,
        string? stdinContent = null,
        bool skipOutputLogging = false)
    {
        StringBuilder output = new();
        StringBuilder error = new();

        var userAndHost = $"{user}@{this.targetIpAddress}";
        var sshArgs = $"-o StrictHostKeyChecking=no " +
            $"-o UserKnownHostsFile=/dev/null " +
            $"-i {privateKeyPath} " +
            $"-o ConnectTimeout=30 " +
            $"{userAndHost} {command}";

        await this.ExecuteCommand("ssh", sshArgs, output, error, skipOutputLogging, stdinContent);
    }

    /// <inheritdoc/>
    public async Task<string> RunCommandWithOutputAsync(
        string user,
        string privateKeyPath,
        string command)
    {
        StringBuilder output = new();
        StringBuilder error = new();

        var userAndHost = $"{user}@{this.targetIpAddress}";
        var sshArgs = $"-o StrictHostKeyChecking=no " +
            $"-o UserKnownHostsFile=/dev/null " +
            $"-i {privateKeyPath} " +
            $"-o ConnectTimeout=30 " +
            $"{userAndHost} {command}";

        await this.ExecuteCommand(
            "ssh", sshArgs, output, error, skipOutputLogging: true);
        return output.ToString();
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // Direct SSH sessions don't have resources to clean up.
        return ValueTask.CompletedTask;
    }
}