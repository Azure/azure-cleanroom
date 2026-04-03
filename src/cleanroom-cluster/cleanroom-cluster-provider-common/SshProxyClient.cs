// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Resources;
using Common;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

/// <summary>
/// Factory for creating SSH proxy sessions using a Kubernetes pod running socat to forward
/// SSH traffic to a target VM. Uses kubectl port-forward to expose the proxy locally.
/// Uses the VM's private IP address for connectivity through the cluster network.
/// </summary>
public class SshProxyClient : RunCommand, ISshSessionFactory
{
    private const string ProxyNamespace = "default";
    private const string ProxyImage = "alpine/socat:latest";
    private const int SshPort = 22;

    private readonly ILogger logger;
    private readonly string kubeConfigFile;

    public SshProxyClient(ILogger logger, string kubeConfigFile)
        : base(logger)
    {
        this.logger = logger;
        this.kubeConfigFile = kubeConfigFile;
    }

    /// <inheritdoc/>
    public async Task<ISshSession> CreateSessionAsync(
        ResourceGroupResource resourceGroupResource,
        VirtualMachineResource vm)
    {
        string privateIpAddress = await this.GetVmPrivateIpAddressAsync(resourceGroupResource, vm);
        this.logger.LogInformation($"Using private IP address for SSH proxy: {privateIpAddress}");

        return await this.CreateAsync(privateIpAddress);
    }

    /// <summary>
    /// Creates an SSH proxy session to the specified target IP address.
    /// </summary>
    /// <param name="targetIpAddress">The IP address of the target VM to SSH into.</param>
    /// <returns>An SshProxySession that can be used to run SSH commands.</returns>
    public async Task<SshProxySession> CreateAsync(string targetIpAddress)
    {
        var uniqueId = Guid.NewGuid().ToString("N")[..8];
        var podName = $"ssh-proxy-{uniqueId}";
        var serviceName = $"ssh-proxy-svc-{uniqueId}";

        this.logger.LogInformation($"Creating SSH proxy to {targetIpAddress}...");

        // Create the pod running socat.
        await this.CreateProxyPodAsync(podName, targetIpAddress);

        // Wait for pod to be ready.
        await this.WaitForPodReadyAsync(podName);

        // Create a service for the pod.
        await this.CreateProxyServiceAsync(podName, serviceName);

        // Start kubectl port-forward.
        int localPort = GetAvailablePort();
        var portForwardProcess = await this.StartPortForwardAsync(podName, localPort);

        this.logger.LogInformation(
            $"SSH proxy created. Connect via: ssh -p {localPort} user@localhost");

        return new SshProxySession(
            this.logger,
            this.kubeConfigFile,
            podName,
            serviceName,
            localPort,
            portForwardProcess);
    }

    private static int GetAvailablePort()
    {
        // Find an available port by binding to port 0.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect("localhost", port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> GetVmPrivateIpAddressAsync(
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
        if (ipConfig == null || string.IsNullOrEmpty(ipConfig.PrivateIPAddress))
        {
            throw new InvalidOperationException(
                $"NIC {nicName} does not have a private IP address.");
        }

        return ipConfig.PrivateIPAddress;
    }

    private async Task CreateProxyPodAsync(string podName, string targetIpAddress)
    {
        this.logger.LogInformation($"Creating SSH proxy pod: {podName}");

        var podYaml = $@"
apiVersion: v1
kind: Pod
metadata:
  name: {podName}
  namespace: {ProxyNamespace}
  labels:
    app: {podName}
spec:
  containers:
  - name: socat
    image: {ProxyImage}
    args:
    - TCP-LISTEN:{SshPort},fork,reuseaddr
    - TCP:{targetIpAddress}:{SshPort}
    ports:
    - containerPort: {SshPort}
      name: ssh
      protocol: TCP
  restartPolicy: Always
";

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, podYaml);
            await this.KubectlAsync($"apply -f {tempFile}");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private async Task WaitForPodReadyAsync(string podName)
    {
        this.logger.LogInformation($"Waiting for pod {podName} to be ready...");

        var maxWait = TimeSpan.FromMinutes(2);
        var elapsed = TimeSpan.Zero;
        var pollInterval = TimeSpan.FromSeconds(5);

        while (elapsed < maxWait)
        {
            try
            {
                var output = new StringBuilder();
                var error = new StringBuilder();
                await this.ExecuteCommand(
                    "kubectl",
                    $"get pod {podName} -n {ProxyNamespace} " +
                    $"-o jsonpath='{{.status.phase}}' --kubeconfig={this.kubeConfigFile}",
                    output,
                    error,
                    skipOutputLogging: true);

                var phase = output.ToString().Trim().Trim('\'');
                if (phase == "Running")
                {
                    this.logger.LogInformation($"Pod {podName} is running.");
                    return;
                }

                this.logger.LogInformation($"Pod status: {phase}, waiting...");
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"Error checking pod status: {ex.Message}");
            }

            await Task.Delay(pollInterval);
            elapsed += pollInterval;
        }

        throw new TimeoutException(
            $"Pod {podName} did not become ready within {maxWait.TotalMinutes} minutes.");
    }

    private async Task CreateProxyServiceAsync(string podName, string serviceName)
    {
        this.logger.LogInformation($"Creating SSH proxy service: {serviceName}");

        var serviceYaml = $@"
apiVersion: v1
kind: Service
metadata:
  name: {serviceName}
  namespace: {ProxyNamespace}
spec:
  selector:
    app: {podName}
  ports:
  - port: {SshPort}
    targetPort: {SshPort}
    protocol: TCP
    name: ssh
  type: ClusterIP
";

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, serviceYaml);
            await this.KubectlAsync($"apply -f {tempFile}");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private async Task<Process> StartPortForwardAsync(string podName, int localPort)
    {
        this.logger.LogInformation(
            $"Starting port-forward: localhost:{localPort} -> {podName}:{SshPort}");

        var args = $"port-forward pod/{podName} {localPort}:{SshPort} " +
            $"-n {ProxyNamespace} --kubeconfig={this.kubeConfigFile}";

        var portForwardProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        portForwardProcess.Start();

        // Wait a bit for port-forward to establish.
        await Task.Delay(TimeSpan.FromSeconds(3));

        if (portForwardProcess.HasExited)
        {
            var error = await portForwardProcess.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Port-forward failed to start: {error}");
        }

        // Verify the port is listening.
        var maxRetries = 10;
        for (int i = 0; i < maxRetries; i++)
        {
            if (IsPortListening(localPort))
            {
                this.logger.LogInformation($"Port-forward established on port {localPort}.");
                return portForwardProcess;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new InvalidOperationException(
            $"Port-forward started but port {localPort} is not listening.");
    }

    private async Task KubectlAsync(string args)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        await this.ExecuteCommand(
            "kubectl",
            $"{args} --kubeconfig={this.kubeConfigFile}",
            output,
            error);
    }
}

/// <summary>
/// Represents an active SSH proxy session. Use this to run SSH commands on the target VM.
/// Dispose of this session when done to clean up Kubernetes resources.
/// </summary>
public class SshProxySession : RunCommand, ISshSession
{
    private const string ProxyNamespace = "default";

    private readonly ILogger logger;
    private readonly string kubeConfigFile;
    private readonly string podName;
    private readonly string serviceName;
    private readonly int localPort;
    private Process? portForwardProcess;
    private bool disposed;

    internal SshProxySession(
        ILogger logger,
        string kubeConfigFile,
        string podName,
        string serviceName,
        int localPort,
        Process portForwardProcess)
        : base(logger)
    {
        this.logger = logger;
        this.kubeConfigFile = kubeConfigFile;
        this.podName = podName;
        this.serviceName = serviceName;
        this.localPort = localPort;
        this.portForwardProcess = portForwardProcess;
    }

    /// <summary>
    /// Gets the local endpoint (localhost:port) to connect to for SSH access.
    /// </summary>
    public string LocalEndpoint => $"localhost:{this.localPort}";

    /// <summary>
    /// Gets the local port used for the SSH proxy.
    /// </summary>
    public int LocalPort => this.localPort;

    public async Task RunCommandAsync(
        string user,
        string privateKeyPath,
        string command,
        string? stdinContent = null,
        bool skipOutputLogging = false)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(SshProxySession));
        }

        StringBuilder output = new();
        StringBuilder error = new();

        var sshArgs = $"-o StrictHostKeyChecking=no " +
            $"-o UserKnownHostsFile=/dev/null " +
            $"-i {privateKeyPath} " +
            $"-o ConnectTimeout=30 " +
            $"-p {this.localPort} " +
            $"{user}@localhost {command}";

        await this.ExecuteCommand("ssh", sshArgs, output, error, skipOutputLogging, stdinContent);
    }

    public async Task<string> RunCommandWithOutputAsync(
        string user,
        string privateKeyPath,
        string command)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(SshProxySession));
        }

        StringBuilder output = new();
        StringBuilder error = new();

        var sshArgs = $"-o StrictHostKeyChecking=no " +
            $"-o UserKnownHostsFile=/dev/null " +
            $"-i {privateKeyPath} " +
            $"-o ConnectTimeout=30 " +
            $"-p {this.localPort} " +
            $"{user}@localhost {command}";

        await this.ExecuteCommand(
            "ssh", sshArgs, output, error, skipOutputLogging: true);
        return output.ToString();
    }

    /// <summary>
    /// Disposes of the SSH proxy resources (pod, service, port-forward process).
    /// </summary>
    /// <returns>A value task representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.logger.LogInformation("Cleaning up SSH proxy resources...");

        // Stop port-forward process.
        if (this.portForwardProcess != null && !this.portForwardProcess.HasExited)
        {
            this.portForwardProcess.Kill();
            this.portForwardProcess.Dispose();
            this.portForwardProcess = null;
        }

        // Delete the service.
        try
        {
            await this.KubectlAsync(
                $"delete service {this.serviceName} -n {ProxyNamespace} --ignore-not-found=true");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning($"Failed to delete service: {ex.Message}");
        }

        // Delete the pod.
        try
        {
            await this.KubectlAsync(
                $"delete pod {this.podName} -n {ProxyNamespace} --ignore-not-found=true " +
                "--grace-period=0 --force");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning($"Failed to delete pod: {ex.Message}");
        }

        this.logger.LogInformation("SSH proxy resources cleaned up.");
    }

    private async Task KubectlAsync(string args)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        await this.ExecuteCommand(
            "kubectl",
            $"{args} --kubeconfig={this.kubeConfigFile}",
            output,
            error);
    }
}
