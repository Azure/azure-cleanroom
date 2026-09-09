// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Common;
using Microsoft.Extensions.Logging;

namespace CcfProvider;

public class HelmImagesClient : RunCommand
{
    private const string RepoName = "release-metadata";
    private const string ChartName = "release-metadata";

    private readonly string repoConfig;
    private readonly string repoCache;

    public HelmImagesClient(ILogger logger)
        : base(logger)
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "release-metadata-helm");
        Directory.CreateDirectory(stateDir);
        this.repoConfig = Path.Combine(stateDir, "repositories.yaml");
        this.repoCache = Path.Combine(stateDir, "cache");
    }

    public async Task<string> ShowValues(string catalogUrl, string releaseVersion)
    {
        string chartRef;
        if (catalogUrl.StartsWith("oci://", StringComparison.OrdinalIgnoreCase))
        {
            // OCI registry: helm resolves the chart directly from the reference, so there is no
            // repo to add/update (the chart lives alongside the images in the same registry).
            chartRef = catalogUrl;
        }
        else
        {
            // HTTP Helm repo (index.yaml): register/refresh it, then address the chart by name.
            await this.Helm($"repo add {RepoName} {catalogUrl} --force-update");
            await this.Helm($"repo update {RepoName}");
            chartRef = $"{RepoName}/{ChartName}";
        }

        var output = new StringBuilder();
        await this.ExecuteCommand(
            "helm",
            $"show values {chartRef} --version {releaseVersion} {this.ConfigFlags()}",
            output,
            new StringBuilder(),
            skipOutputLogging: true);
        return output.ToString();
    }

    private string ConfigFlags()
    {
        return $"--repository-config {this.repoConfig} --repository-cache {this.repoCache}";
    }

    private Task<int> Helm(string args)
    {
        return this.ExecuteCommand("helm", $"{args} {this.ConfigFlags()}");
    }
}
