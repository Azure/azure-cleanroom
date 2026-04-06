// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CleanRoomProvider;

public class HelmClient : RunCommand
{
    private static SemaphoreSlim semaphore = new(1, 1);

    private readonly ILogger logger;
    private readonly IConfiguration config;
    private readonly string kubeConfigFile;

    public HelmClient(ILogger logger, IConfiguration config, string kubeConfigFile)
        : base(logger)
    {
        this.logger = logger;
        this.config = config;
        this.kubeConfigFile = kubeConfigFile;
    }

    public async Task InstallVN2Chart(string release)
    {
        // https://github.com/microsoft/virtualnodesOnAzureContainerInstances/blob/main/Docs/NodeCustomizations.md#node-customizations
        await this.HelmRepoAdd(
            "vn2 https://microsoft.github.io/virtualnodesOnAzureContainerInstances");
        var command = $"upgrade {release} " +
            $"vn2/virtualnode " +
            $"--install " +
            $"--version 1.3307.26011601 " +
            $"--namespace vn2-release " +
            $"--create-namespace " +
            $"--kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);
    }

    public async Task InstallSparkOperatorChart(string release)
    {
        // https://www.kubeflow.org/docs/components/spark-operator/getting-started/#add-helm-repo
        var valuesFile = "spark-operator/values.yaml";
        await this.HelmRepoAdd("spark-operator https://kubeflow.github.io/spark-operator");
        var command = $"upgrade {release} " +
            $"spark-operator/spark-operator " +
            $"--install " +
            $"--version 2.3.0 " +
            $"--namespace spark-operator " +
            $"--create-namespace " +
            $"--values {valuesFile} " +
            $"--kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallCertManagerChart(string release)
    {
        string version = KServeDeps.GetCertManagerVersion(this.logger);
        await this.HelmRepoAdd("jetstack https://charts.jetstack.io");
        var command = $"upgrade {release} " +
            $"jetstack/cert-manager " +
            $"--install " +
            $"--version {version} " +
            $"--namespace cert-manager " +
            $"--create-namespace " +
            $"--set crds.enabled=true " +
            $"--kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);
    }

    public async Task InstallKServe()
    {
        string version = KServeDeps.KServeVersion;
        string release = "kserve-crd";
        var command = $"upgrade {release} " +
            $"oci://ghcr.io/kserve/charts/kserve-crd " +
            $"--install " +
            $"--version {version} " +
            $"--namespace kserve " +
            $"--create-namespace " +
            $"--kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);

        // https://github.com/kserve/kserve/issues/5210
        // kserve-crd helm upgrade can delete the ClusterStorageContainer CRD.
        // If missing, retry the upgrade once to restore it.
        string crd = "clusterstoragecontainers.serving.kserve.io";
        if (!await CrdExists(crd))
        {
            this.logger.LogWarning(
                "ClusterStorageContainer CRD not found after kserve-crd upgrade. " +
                "Retrying upgrade to restore it.");
            await this.Helm(command);
            if (!await CrdExists(crd))
            {
                throw new InvalidOperationException(
                    "ClusterStorageContainer CRD still not found after retrying " +
                    "kserve-crd upgrade. See https://github.com/kserve/kserve/issues/5210.");
            }
        }

        release = "kserve-resources";
        command = $"upgrade {release} " +
            $"oci://ghcr.io/kserve/charts/kserve-resources " +
            $"--install " +
            $"--version {version} " +
            $"--namespace kserve " +
            $"--set kserve.controller.deploymentMode=Standard " +
            $"--wait " +
            $"--kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);

        async Task<bool> CrdExists(string crdName)
        {
            try
            {
                var binary = Environment.ExpandEnvironmentVariables(
                    this.config["KUBECTL_PATH"] ?? "kubectl");
                StringBuilder output = new();
                StringBuilder error = new();
                await this.ExecuteCommand(
                    binary,
                    $"get crd {crdName} --kubeconfig={this.kubeConfigFile}",
                    output,
                    error,
                    skipOutputLogging: true);
                return true;
            }
            catch (ExecuteCommandException)
            {
                return false;
            }
        }
    }

    public async Task InstallPrometheusChart(
        string release,
        string ns,
        List<string>? valuesOverrideFiles = null)
    {
        // https://github.com/prometheus-community/helm-charts
        var valuesFile = "observability/prometheus/values.yaml";
        await this.HelmRepoAdd(
            "prometheus-community https://prometheus-community.github.io/helm-charts");
        var command = $"upgrade {release} " +
            $"prometheus-community/prometheus " +
            $"--install " +
            $"--version 27.28.1 " +
            $"--namespace {ns} " +
            $"--create-namespace " +
            $"--values {valuesFile}";
        valuesOverrideFiles ??= new List<string>();
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallLokiChart(
        string release,
        string ns,
        List<string>? valuesOverrideFiles = null)
    {
        // https://grafana.com/docs/loki/latest/setup/install/helm/install-monolithic/#deploying-the-helm-chart-for-development-and-testing
        var valuesFile = "observability/loki/values.yaml";
        await this.HelmRepoAdd("grafana https://grafana.github.io/helm-charts");
        var command = $"upgrade {release} " +
            $"grafana/loki " +
            $"--install " +
            $"--version 6.33.0 " +
            $"--namespace {ns} " +
            $"--create-namespace " +
            $"--values {valuesFile}";
        valuesOverrideFiles ??= new List<string>();
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallTempoChart(
        string release,
        string ns,
        List<string>? valuesOverrideFiles = null)
    {
        // https://github.com/grafana/helm-charts/tree/main/charts/tempo#chart-repo
        var valuesFile = "observability/tempo/values.yaml";
        await this.HelmRepoAdd("grafana https://grafana.github.io/helm-charts");
        var command = $"upgrade {release} " +
            $"grafana/tempo " +
            $"--install " +
            $"--version 1.23.2 " +
            $"--namespace {ns} " +
            $"--create-namespace " +
            $"--values {valuesFile}";
        valuesOverrideFiles ??= new List<string>();
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);
    }

    public async Task InstallGrafanaChart(
        string release,
        string ns,
        List<string>? valuesOverrideFiles = null)
    {
        // https://grafana.com/docs/grafana/latest/administration/helm-chart/
        var valuesFile = "observability/grafana/values.yaml";
        await this.HelmRepoAdd("grafana https://grafana.github.io/helm-charts");
        var command = $"upgrade {release} " +
            $"grafana/grafana " +
            $"--install " +
            $"--version 9.2.10 " +
            $"--namespace {ns} " +
            $"--create-namespace " +
            $"--values {valuesFile}";
        valuesOverrideFiles ??= new List<string>();
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);
    }

    public async Task InstallAnalyticsAgentChart(
        string release,
        string ns,
        List<string> valuesOverrideFiles,
        Dictionary<string, string>? serviceAnnotations = null)
    {
        string? serviceAnnotationValuesFiles = null;
        if (serviceAnnotations != null && serviceAnnotations.Any())
        {
            serviceAnnotationValuesFiles = Path.GetTempFileName();
            var values = new HelmChartAnalyticsAgentServiceValues
            {
                Service = new()
                {
                    Annotations = serviceAnnotations
                }
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(values);
            await File.WriteAllTextAsync(serviceAnnotationValuesFiles, yaml);
        }

        var chartPath = $"oci://{ImageUtils.GetAnalyticsAgentChartPath()}";
        var chartVersion = ImageUtils.GetAnalyticsAgentChartVersion();
        var command = $"upgrade {release} " +
            $"{chartPath} " +
            $"--install " +
            $"--version {chartVersion} " +
            $"--namespace {ns} " +
            $"--create-namespace";
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        if (serviceAnnotationValuesFiles != null)
        {
            command += $" --values {serviceAnnotationValuesFiles}";
        }

        if (ImageUtils.GetAnalyticsAgentChartPath().StartsWith("localhost:"))
        {
            command += $" --plain-http";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallSparkFrontendChart(
        string release,
        string ns,
        List<string> valuesOverrideFiles,
        Dictionary<string, string>? serviceAnnotations = null)
    {
        string? serviceAnnotationValuesFiles = null;
        if (serviceAnnotations != null && serviceAnnotations.Any())
        {
            serviceAnnotationValuesFiles = Path.GetTempFileName();
            var values = new HelmChartAnalyticsAgentServiceValues
            {
                Service = new()
                {
                    Annotations = serviceAnnotations
                }
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(values);
            await File.WriteAllTextAsync(serviceAnnotationValuesFiles, yaml);
        }

        var chartPath = $"oci://{ImageUtils.GetSparkFrontendChartPath()}";
        var chartVersion = ImageUtils.GetSparkFrontendChartVersion();
        var command = $"upgrade {release} " +
            $"{chartPath} " +
            $"--install " +
            $"--version {chartVersion} " +
            $"--namespace {ns} " +
            $"--create-namespace";
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        if (serviceAnnotationValuesFiles != null)
        {
            command += $" --values {serviceAnnotationValuesFiles}";
        }

        if (ImageUtils.GetSparkFrontendChartPath().StartsWith("localhost:"))
        {
            command += $" --plain-http";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallInferencingAgentChart(
        string release,
        string ns,
        List<string> valuesOverrideFiles,
        Dictionary<string, string>? serviceAnnotations = null)
    {
        string? serviceAnnotationValuesFiles = null;
        if (serviceAnnotations != null && serviceAnnotations.Any())
        {
            serviceAnnotationValuesFiles = Path.GetTempFileName();
            var values = new HelmChartAnalyticsAgentServiceValues
            {
                Service = new()
                {
                    Annotations = serviceAnnotations
                }
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(values);
            await File.WriteAllTextAsync(serviceAnnotationValuesFiles, yaml);
        }

        var chartPath = $"oci://{ImageUtils.GetInferencingAgentChartPath()}";
        var chartVersion = ImageUtils.GetInferencingAgentChartVersion();
        var command = $"upgrade {release} " +
            $"{chartPath} " +
            $"--install " +
            $"--version {chartVersion} " +
            $"--namespace {ns} " +
            $"--create-namespace";
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        if (serviceAnnotationValuesFiles != null)
        {
            command += $" --values {serviceAnnotationValuesFiles}";
        }

        if (ImageUtils.GetInferencingAgentChartPath().StartsWith("localhost:"))
        {
            command += $" --plain-http";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallInferencingFrontendChart(
        string release,
        string ns,
        List<string> valuesOverrideFiles,
        Dictionary<string, string>? serviceAnnotations = null)
    {
        string? serviceAnnotationValuesFiles = null;
        if (serviceAnnotations != null && serviceAnnotations.Any())
        {
            serviceAnnotationValuesFiles = Path.GetTempFileName();
            var values = new HelmChartAnalyticsAgentServiceValues
            {
                Service = new()
                {
                    Annotations = serviceAnnotations
                }
            };

            var serializer = new SerializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();
            var yaml = serializer.Serialize(values);
            await File.WriteAllTextAsync(serviceAnnotationValuesFiles, yaml);
        }

        var chartPath = $"oci://{ImageUtils.GetInferencingFrontendChartPath()}";
        var chartVersion = ImageUtils.GetInferencingFrontendChartVersion();
        var command = $"upgrade {release} " +
            $"{chartPath} " +
            $"--install " +
            $"--version {chartVersion} " +
            $"--namespace {ns} " +
            $"--create-namespace";
        foreach (var file in valuesOverrideFiles)
        {
            command += $" --values {file}";
        }

        if (serviceAnnotationValuesFiles != null)
        {
            command += $" --values {serviceAnnotationValuesFiles}";
        }

        if (ImageUtils.GetInferencingFrontendChartPath().StartsWith("localhost:"))
        {
            command += $" --plain-http";
        }

        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallExternalDnsChart(
        string release,
        string ns,
        string wiClientId)
    {
        var template = await File.ReadAllTextAsync("external-dns/values.yaml");
        template = template.Replace("<CLIENT_ID>", wiClientId);
        var valuesFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(valuesFile, template);

        await this.HelmRepoAdd("external-dns https://kubernetes-sigs.github.io/external-dns/");
        var command = $"upgrade {release} external-dns/external-dns " +
            $"--install " +
            $"--version 1.19.0 " +
            $"--namespace {ns} " +
            $"--create-namespace " +
            $"--values {valuesFile}";
        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallKaitoChart(
        string release,
        string ns)
    {
        // https://kaito-project.github.io/kaito/docs/installation#install-kaito-workspace-controller
        await this.HelmRepoAdd("kaito https://kaito-project.github.io/kaito/charts/kaito");
        var command = $"upgrade {release} " +
            $"kaito/workspace " +
            $"--install " +
            $"--version 0.7.2 " +
            $"--namespace {ns} " +
            $"--create-namespace " +
            $"--kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);
    }

    private async Task HelmRepoAdd(string args)
    {
        try
        {
            // Avoid simultaneous repo update commands as at times we see following error if
            // multiple helm add/update are running in parallel:
            // 'Error: no cached repo found. (try 'helm repo update'): error loading
            // /root/.cache/helm/repository/prometheus-community-index.yaml: empty index.yaml file'.
            await semaphore.WaitAsync();
            await this.Helm($"repo add {args}");
            await this.Helm("repo update");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private Task<int> Helm(string args)
    {
        var binary = Environment.ExpandEnvironmentVariables(this.config["HELM_PATH"] ?? "helm");
        return this.ExecuteCommand(binary, args);
    }

    public class HelmChartAnalyticsAgentServiceValues
    {
        public HelmChartAnalyticsAgentService Service { get; set; } = default!;

        public class HelmChartAnalyticsAgentService
        {
            public Dictionary<string, string> Annotations { get; set; } = default!;
        }
    }
}
