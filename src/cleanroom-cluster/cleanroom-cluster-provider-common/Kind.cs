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

    public async Task CreateCluster(string name)
    {
        await this.Kind($"create cluster --name {name} --config=kind/kind-config.yaml");
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