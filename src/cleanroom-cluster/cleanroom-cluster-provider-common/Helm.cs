// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CleanRoomProvider;

public class HelmClient : RunCommand
{
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
        var chartPath = Environment.ExpandEnvironmentVariables(
            this.config["VN2_CHART_PATH"] ??
            "/virtualnodesOnAzureContainerInstances/Helm/virtualnode");
        var command = $"upgrade {release} {chartPath} --install --namespace vn2-release " +
            $"--create-namespace --kubeconfig {this.kubeConfigFile}";
        await this.Helm(command);
    }

    public async Task InstallSparkOperatorChart(string release)
    {
        // https://www.kubeflow.org/docs/components/spark-operator/getting-started/#add-helm-repo
        var valuesFile = "spark-operator/values.yaml";
        await this.Helm("repo add spark-operator https://kubeflow.github.io/spark-operator");
        await this.Helm("repo update");
        var command = $"upgrade {release} spark-operator/spark-operator --install " +
            $"--version 2.1.1 --namespace spark-operator --create-namespace --values {valuesFile}";
        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
    }

    public async Task InstallPrometheusChart(
        string release,
        string ns,
        List<string>? valuesOverrideFiles = null)
    {
        // https://github.com/prometheus-community/helm-charts
        var valuesFile = "observability/prometheus/values.yaml";
        await this.Helm(
            "repo add prometheus-community https://prometheus-community.github.io/helm-charts");
        await this.Helm("repo update");
        var command = $"upgrade {release} prometheus-community/prometheus --install " +
            $"--version 27.28.1 --namespace {ns} --create-namespace --values {valuesFile}";
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
        await this.Helm(
            "repo add grafana https://grafana.github.io/helm-charts");
        await this.Helm("repo update");
        var command = $"upgrade {release} grafana/loki --install " +
            $"--version 6.33.0 --namespace {ns} --create-namespace --values {valuesFile}";
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
        await this.Helm(
            "repo add grafana https://grafana.github.io/helm-charts");
        await this.Helm("repo update");
        var command = $"upgrade {release} grafana/tempo --install " +
            $"--version 1.23.2 --namespace {ns} --create-namespace --values {valuesFile}";
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
        await this.Helm(
            "repo add grafana https://grafana.github.io/helm-charts");
        await this.Helm("repo update");
        var command = $"upgrade {release} grafana/grafana --install " +
            $"--version 9.2.10 --namespace {ns} --create-namespace --values {valuesFile}";
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
            $"--create-namespace ";
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
            $"--create-namespace ";
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

    public async Task InstallExternalDnsChart(
        string release,
        string ns,
        string wiClientId)
    {
        var template = await File.ReadAllTextAsync("external-dns/values.yaml");
        template = template.Replace("<CLIENT_ID>", wiClientId);
        var valuesFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(valuesFile, template);

        await this.Helm("repo add external-dns https://kubernetes-sigs.github.io/external-dns/");
        await this.Helm("repo update");
        var command = $"upgrade {release} external-dns/external-dns --install " +
            $"--version 1.18.0 --namespace {ns} --create-namespace --values {valuesFile}";
        command += $" --kubeconfig {this.kubeConfigFile}";

        await this.Helm(command);
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