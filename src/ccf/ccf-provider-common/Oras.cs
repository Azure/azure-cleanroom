// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Common;
using Microsoft.Extensions.Logging;

namespace CcfProvider;

public class OrasClient : RunCommand
{
    public OrasClient(ILogger logger)
        : base(logger)
    {
    }

    public async Task Pull(string registryUrl, string outDir)
    {
        await this.Oras($"pull {registryUrl} -o {outDir}");
    }

    private Task<int> Oras(string args)
    {
        return this.ExecuteCommand("oras", args);
    }
}
