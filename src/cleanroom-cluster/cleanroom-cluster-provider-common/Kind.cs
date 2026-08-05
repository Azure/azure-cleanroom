// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

public class KindClient : RunCommand
{
    private IConfiguration config;

    public KindClient(ILogger logger, IConfiguration config)
        : base(logger)
    {
        this.config = config;
    }

    public async Task CreateCluster(string name, int flexNodeCount = 0)
    {
        var template = await File.ReadAllTextAsync("kind/kind-config.yaml");
        var hostSharedDir =
            Environment.GetEnvironmentVariable("CR_CLUSTER_PROVIDER_HOST_SHARED_DIR") ??
            throw new ArgumentNullException("CR_CLUSTER_PROVIDER_HOST_SHARED_DIR");
        template = template.Replace("<HOST_SHARED_DIR>", hostSharedDir);

        var flexNodeWorkers = new StringBuilder();
        if (flexNodeCount > 0)
        {
            const string flexNodeEntry =
                """
                  - role: worker
                    kubeadmConfigPatches:
                      - |
                        kind: JoinConfiguration
                        nodeRegistration:
                          taints:
                            - key: "for-flex-node"
                              value: "true"
                              effect: "NoSchedule"

                """;
            for (int i = 0; i < flexNodeCount; i++)
            {
                flexNodeWorkers.Append(flexNodeEntry);
            }
        }

        template = template.Replace("  # <FLEX_NODE_WORKERS>\n", flexNodeWorkers.ToString());

        var configFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(configFile, template);
        await this.Kind($"create cluster --name {name} --config={configFile}");
    }

    public async Task DeleteCluster(string name)
    {
        await this.Kind($"delete cluster --name {name}");
    }

    public async Task<string> GetKubeConfig(string name, bool withInternalAddress = false)
    {
        var args = $"get kubeconfig --name {name}";
        if (withInternalAddress)
        {
            args += " --internal";
        }

        var output = await this.Kind(args, skipOutputLogging: true);
        return output;
    }

    public async Task<bool> ClusterExists(string name)
    {
        var args = $"get clusters";
        var output = await this.Kind(args, skipOutputLogging: true);
        return output.Split("\n").Contains(name);
    }

    public async Task<List<string>> GetNodeNames(string name)
    {
        var nodeNames = await this.Kind($"get nodes --name {name}");
        var nodes = nodeNames.Split("\n").Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return nodes.Select(x => x.Trim()).ToList();
    }

    public async Task LoadImageArchive(string imageFile, string name, string nodeName)
    {
        await this.Kind($"load image-archive {imageFile} --name {name} --nodes {nodeName}");
    }

    public async Task AddWorkerNode(
        string clusterName,
        string nodeName,
        string? taint = null,
        string? providerID = null)
    {
        var args = $"kind/add-worker-node.sh --cluster-name {clusterName} --node-name {nodeName}";
        if (!string.IsNullOrEmpty(taint))
        {
            args += $" --add-taint {taint}";
        }

        if (!string.IsNullOrEmpty(providerID))
        {
            args += $" --provider-id {providerID}";
        }

        StringBuilder output = new();
        StringBuilder error = new();
        await this.ExecuteCommand("bash", args, output, error);
    }

    private async Task<string> Kind(
        string args,
        bool skipOutputLogging = false)
    {
        StringBuilder output = new();
        StringBuilder error = new();
        var binary = Environment.ExpandEnvironmentVariables(this.config["KIND_PATH"] ?? "kind");
        await this.ExecuteCommand(binary, args, output, error, skipOutputLogging);
        return output.ToString();
    }
}