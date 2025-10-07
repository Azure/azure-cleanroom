// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Common;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

// For when using Docker.DotNet gets too complicated to do things eg Copy() command below.
public class DockerCliClient : RunCommand
{
    public DockerCliClient(ILogger logger)
        : base(logger)
    {
    }

    public async Task Exec(string containerId, string command)
    {
        await this.Docker($"exec {containerId} {command}");
    }

    public async Task Copy(string containerId, string sourcePath, string containerPath)
    {
        await this.Docker($"cp {sourcePath} {containerId}:{containerPath}");
    }

    private async Task<(int, string, string)> Docker(string args, bool skipOutputLogging = false)
    {
        StringBuilder output = new();
        StringBuilder error = new();
        var binary = "docker";

        return (
            await this.ExecuteCommand(binary, args, output, error, skipOutputLogging),
            output.ToString(), error.ToString());
    }
}
