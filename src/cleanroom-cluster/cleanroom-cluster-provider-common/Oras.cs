// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

public class OrasClient : RunCommand
{
    private readonly IConfiguration config;

    public OrasClient(ILogger logger, IConfiguration config)
        : base(logger)
    {
        this.config = config;
    }

    public async Task Pull(string registryUrl, string outDir)
    {
        await this.Oras($"pull {registryUrl} -o {outDir}");
    }

    private async Task<(int, string, string)> Oras(string args, bool skipOutputLogging = false)
    {
        StringBuilder output = new();
        StringBuilder error = new();
        var binary = Environment.ExpandEnvironmentVariables(
            this.config["ORAS_PATH"] ?? "oras");

        return (
            await this.ExecuteCommand(binary, args, output, error, skipOutputLogging),
            output.ToString(), error.ToString());
    }
}
