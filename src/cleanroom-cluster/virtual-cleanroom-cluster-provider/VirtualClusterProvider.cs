// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CleanRoomProvider;
using Common;
using Controllers;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace VirtualCleanRoomProvider;

public class VirtualClusterProvider : ICleanRoomClusterProvider
{
    private HttpClientManager httpClientManager;
    private ILogger logger;
    private IConfiguration configuration;
    private DockerClient dockerClient;

    public VirtualClusterProvider(
        ILogger logger,
        IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
        this.dockerClient = new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock"))
            .CreateClient();
        this.httpClientManager = new(logger);
    }

    public InfraType InfraType => InfraType.@virtual;

    public async Task<CleanRoomCluster> CreateCluster(
        string clClusterName,
        CleanRoomClusterInput input,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        // Create a kind cluster.
        var kindClient = new KindClient(this.logger, this.configuration);
        string kindClusterName = this.ToKindClusterName(clClusterName);
        string outDir = Path.GetTempPath();
        var kubeConfigFile = Path.Combine(outDir, $"{kindClusterName}.config");
        progressReporter.Report("Creating kind cluster...");
        this.logger.LogInformation($"Starting kind cluster creation: {kindClusterName}");
        try
        {
            await kindClient.CreateCluster(kindClusterName);
            this.logger.LogInformation($"Kind cluster creation succeeded: {kindClusterName}");
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("failed to create cluster: node(s) already exist for a cluster" +
            " with the name"))
        {
            // Cluster already exists. Ignore.
            this.logger.LogInformation(
                $"Found existing kind cluster so skipping creation: {kindClusterName}");
        }

        // If running in a container then get the kubeconfig with the internal name for the api
        // server eg:
        // https://testpool-virtual-kind-control-plane:6443 so that kind can reach it.
        bool withInternalAddress =
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        var kubeConfig = await kindClient.GetKubeConfig(kindClusterName, withInternalAddress);
        await File.WriteAllTextAsync(kubeConfigFile, kubeConfig);

        // Connect the kind control plane container to the docker network of this container
        // so that the api server via the internal address is reachable from this container.
        if (withInternalAddress)
        {
            var containerId = Environment.GetEnvironmentVariable("HOSTNAME")!;
            var container = await this.GetContainerById(containerId);
            var networkName = container.NetworkSettings.Networks.Single().Key;
            var kindContainerNames = await kindClient.GetNodeNames(kindClusterName);
            foreach (var kindContainerName in kindContainerNames)
            {
                var kindContainer = await this.GetContainerByName(kindContainerName);
                if (!kindContainer.NetworkSettings.Networks.Keys.Contains(networkName))
                {
                    this.logger.LogInformation(
                        $"Connecting {kindContainerName} to network {networkName}.");
                    await this.dockerClient.Networks.ConnectNetworkAsync(
                        networkName,
                        new NetworkConnectParameters
                        {
                            Container = kindContainerName
                        });
                }
            }
        }

        // Connect the kind cluster network with the ccr-container registry container
        // so that kind is able to pull images from "localhost:5000".
        await ConnectKindToLocalContainerRegistry();

        if (input.ObservabilityProfile != null && input.ObservabilityProfile.Enabled)
        {
            this.logger.LogInformation($"Enabling telemetry for cluster: {clClusterName}");
            await this.EnableClusterObservabilityAsync(
                clClusterName,
                progressReporter);
        }

        // Install Spark Operator and wait for it to be ready.
        progressReporter.Report("Installing spark-operator...");
        this.logger.LogInformation(
            $"Starting installation of Spark-Operator helm chart on: {kindClusterName}");
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallSparkOperatorChart("spark-operator");
        this.logger.LogInformation($"Spark-Operator helm chart installation succeeded.");

        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        progressReporter.Report("Waiting for spark-operator to become ready...");
        this.logger.LogInformation($"Waiting for Spark-Operator pod/deployment to become ready.");
        await kubectlClient.WaitForSparkOperatorUp("spark-operator");
        this.logger.LogInformation($"Spark-Operator pod/deployment are reporting ready.");

        if (input.AnalyticsWorkloadProfile != null && input.AnalyticsWorkloadProfile.Enabled)
        {
            await this.EnableAnalyticsWorkloadAsync(
                clClusterName,
                input.AnalyticsWorkloadProfile,
                Constants.AnalyticsWorkloadNamespace,
                progressReporter);
        }

        progressReporter.Report($"Cluster creation completed.");
        this.logger.LogInformation($"Cluster creation completed: {clClusterName}");
        return await this.GetCluster(clClusterName, providerConfig);

        async Task ConnectKindToLocalContainerRegistry()
        {
            string registryName = "ccr-registry";
            var registryContainer = await this.TryGetContainerByName(registryName);
            if (registryContainer == null)
            {
                return;
            }

            // Step 3 onwards of https://kind.sigs.k8s.io/docs/user/local-registry/.
            string registryPort = "5000";
            string registryDir = $"/etc/containerd/certs.d/localhost:{registryPort}";

            var tomlFile = Path.GetTempPath() + "hosts.toml";
            await File.WriteAllTextAsync(
                tomlFile,
                $@"
[host.""http://{registryName}:{registryPort}""]
");
            var kindContainerNames = await kindClient.GetNodeNames(kindClusterName);
            var dockerCliClient = new DockerCliClient(this.logger);
            foreach (var kindContainerName in kindContainerNames)
            {
                var kindContainer = await this.GetContainerByName(kindContainerName);
                await dockerCliClient.Exec(kindContainer.ID, $"mkdir -p {registryDir}");
                await dockerCliClient.Copy(kindContainer.ID, tomlFile, $"{registryDir}/hosts.toml");
            }

            if (!registryContainer.NetworkSettings.Networks.Keys.Contains("kind"))
            {
                this.logger.LogInformation(
                    $"Connecting {registryName} to network kind.");
                await this.dockerClient.Networks.ConnectNetworkAsync(
                    "kind",
                    new NetworkConnectParameters
                    {
                        Container = registryName
                    });
            }

            var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
            string yamlFile = Path.GetTempPath() + "local-registry-hosting.yaml";
            await File.WriteAllTextAsync(
                yamlFile,
                $@"
apiVersion: v1
kind: ConfigMap
metadata:
  name: local-registry-hosting
  namespace: kube-public
data:
  localRegistryHosting.v1: |
    host: ""localhost:${registryPort}""
    help: ""https://kind.sigs.k8s.io/docs/user/local-registry/""
");
            await kubectlClient.ApplyAsync(yamlFile);

            yamlFile = Path.GetTempPath() + "ccr-registry-conf.yaml";
            await File.WriteAllTextAsync(
                yamlFile,
                $@"
apiVersion: v1
kind: ConfigMap
metadata:
  name: ccr-registry-conf
  namespace: default
data:
  ccr-registry.conf: |
    [[registry]]
    location = ""${registryName}:5000""
    insecure = true
");
            await kubectlClient.ApplyAsync(yamlFile);
        }
    }

    public async Task<CleanRoomCluster?> UpdateCluster(
    string clClusterName,
    CleanRoomClusterInput input,
    JsonObject? providerConfig,
    IProgress<string> progressReporter)
    {
        string kindClusterName = this.ToKindClusterName(clClusterName);
        var kindClient = new KindClient(this.logger, this.configuration);
        if (!await kindClient.ClusterExists(kindClusterName))
        {
            return null;
        }

        if (input.ObservabilityProfile != null && input.ObservabilityProfile.Enabled)
        {
            this.logger.LogInformation($"Enabling telemetry for cluster: {clClusterName}");
            await this.EnableClusterObservabilityAsync(
                clClusterName,
                progressReporter);
        }

        if (input.AnalyticsWorkloadProfile != null && input.AnalyticsWorkloadProfile.Enabled)
        {
            await this.EnableAnalyticsWorkloadAsync(
                clClusterName,
                input.AnalyticsWorkloadProfile,
                Constants.AnalyticsWorkloadNamespace,
                progressReporter);
        }

        progressReporter.Report($"Cluster update completed.");
        this.logger.LogInformation($"Cluster update completed: {clClusterName}");
        return await this.GetCluster(clClusterName, providerConfig);
    }

    public async Task DeleteCluster(string clusterName, JsonObject? providerConfig)
    {
        string kindClusterName = this.ToKindClusterName(clusterName);
        var kindClient = new KindClient(this.logger, this.configuration);
        await kindClient.DeleteCluster(kindClusterName);
    }

    public async Task<CleanRoomCluster> GetCluster(
        string clClusterName,
        JsonObject? providerConfig)
    {
        return await this.TryGetCluster(clClusterName, providerConfig) ??
            throw new Exception($"No cluster found for {clClusterName}.");
    }

    public async Task<CleanRoomClusterKubeConfig?> TryGetClusterKubeConfig(
        string clClusterName,
        JsonObject? providerConfig)
    {
        string kindClusterName = this.ToKindClusterName(clClusterName);
        var kindClient = new KindClient(this.logger, this.configuration);

        try
        {
            var kubeConfig = await kindClient.GetKubeConfig(kindClusterName);
            return new CleanRoomClusterKubeConfig
            {
                Kubeconfig = Encoding.UTF8.GetBytes(kubeConfig)
            };
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("could not locate any control plane nodes for cluster " +
        $"named '{kindClusterName}'"))
        {
            return null;
        }
    }

    public async Task<CleanRoomCluster?> TryGetCluster(
        string clClusterName,
        JsonObject? providerConfig)
    {
        string kindClusterName = this.ToKindClusterName(clClusterName);
        var kindClient = new KindClient(this.logger, this.configuration);
        if (!await kindClient.ClusterExists(kindClusterName))
        {
            return null;
        }

        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        (bool analyticsWorkloadEnabled, string? analyticsAgentEndpoint) =
            await kubectlClient.TryGetAnalyticsAgentEndpoint(Constants.AnalyticsAgentNamespace);
        (bool observabilityEnabled, string? observabilityEndpoint) =
            await kubectlClient.TryGetObservabilityEndpoint(Constants.ObservabilityNamespace);
        return this.ToCleanRoomCluster(
            clClusterName,
            kindClusterName,
            analyticsWorkloadEnabled,
            analyticsAgentEndpoint,
            observabilityEnabled,
            observabilityEndpoint);
    }

    public async Task<AnalyticsWorkloadGeneratedDeployment> GenerateAnalyticsWorkloadDeployment(
        GenerateAnalyticsWorkloadDeploymentInput input,
        JsonObject? providerConfig)
    {
        var policyOption = SecurityPolicyConfigInput.Convert(input.SecurityPolicy);
        var contractData = await this.httpClientManager.GetContractData(
            this.logger,
            input.ContractUrl,
            input.ContractUrlCaCert);

        var telemetryCollectionEnabled =
            input.TelemetryProfile != null && input.TelemetryProfile.CollectionEnabled;
        Console.WriteLine($"Telemetry collection enabled: {telemetryCollectionEnabled}");
        return new AnalyticsWorkloadGeneratedDeployment
        {
            SecurityPolicyCreationOption = policyOption.PolicyCreationOption.ToString(),
            DeploymentTemplate = new()
            {
                ChartMetadata = new()
                {
                    Chart = ImageUtils.GetAnalyticsAgentChartPath(),
                    Version = ImageUtils.GetAnalyticsAgentChartVersion(),
                    Release = Constants.AnalyticsAgentReleaseName,
                    Namespace = Constants.AnalyticsAgentNamespace
                },
                Values = AgentChartValues.ToAgentChartValues(
                    contractData,
                    telemetryCollectionEnabled,
                    Constants.SparkFrontendEndpoint,
                    AciConstants.AllowAllPolicyDigest)
            },
            GovernancePolicy = new()
            {
                Type = "add",
                Claims = new()
                {
                    IsDebuggable = false,
                    HostData = AciConstants.AllowAllPolicyDigest
                }
            },
            CcePolicy = new()
            {
                Value = AciConstants.AllowAllPolicyRegoBase64,
                DocumentUrl = ImageUtils.GetAnalyticsAgentSecurityPolicyDocumentUrl()
            }
        };
    }

    private async Task<string> GetInternalKubeConfigFileAsync(string kindClusterName)
    {
        var kindClient = new KindClient(this.logger, this.configuration);
        bool withInternalAddress =
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        var kubeConfig = await kindClient.GetKubeConfig(kindClusterName, withInternalAddress);
        string kubeConfigFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(kubeConfigFile, kubeConfig);
        return kubeConfigFile;
    }

    private CleanRoomCluster ToCleanRoomCluster(
        string clClusterName,
        string kindClusterName,
        bool analyticsWorkloadEnabled,
        string? analyticsAgentEndpoint,
        bool observabilityEnabled,
        string? observabilityEndpoint)
    {
        return new CleanRoomCluster
        {
            Name = clClusterName,
            InfraType = this.InfraType.ToString(),
            ObservabilityProfile = new ObservabilityProfile
            {
                Enabled = observabilityEnabled,
                VisualizationEndpoint = observabilityEndpoint,
                MetricsEndpoint = observabilityEnabled ?
                    Constants.PrometheusServiceEndpoint : null,
                LogsEndpoint = observabilityEnabled ?
                    Constants.LokiServiceEndpoint : null,
                TracesEndpoint = observabilityEnabled ?
                    Constants.TempoServiceEndpoint : null
            },
            AnalyticsWorkloadProfile = new AnalyticsWorkloadProfile
            {
                Enabled = analyticsWorkloadEnabled,
                Namespace = analyticsWorkloadEnabled ? Constants.AnalyticsWorkloadNamespace : null,
                Endpoint = analyticsAgentEndpoint
            },
            ProviderProperties = new JsonObject
            {
                ["kindClusterName"] = kindClusterName,
                ["kubernetesMasterFqdn"] = "kubernetes.default.svc"
            }
        };
    }

    private string ToKindClusterName(string input)
    {
        return input + "-kind";
    }

    private async Task EnableClusterObservabilityAsync(
        string clClusterName,
        IProgress<string> progressReporter)
    {
        string kindClusterName = this.ToKindClusterName(clClusterName);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);

        this.logger.LogInformation($"Creating namespace {Constants.ObservabilityNamespace}.");
        await kubectlClient.CreateNamespaceAsync(Constants.ObservabilityNamespace);
        this.logger.LogInformation($"Namespace created.");
        progressReporter.Report("Installing prometheus, loki, tempo and grafana...");

        var prometheusTask = helmClient.InstallPrometheusChart(
            Constants.PrometheusReleaseName,
            Constants.ObservabilityNamespace);
        var lokiTask = helmClient.InstallLokiChart(
            Constants.LokiReleaseName,
            Constants.ObservabilityNamespace);
        var tempoTask = helmClient.InstallTempoChart(
            Constants.TempoReleaseName,
            Constants.ObservabilityNamespace);
        var grafanaDashboardsTask = kubectlClient.InstallGrafanaDashboards(
            Constants.ObservabilityNamespace);
        var grafanaChartTask = helmClient.InstallGrafanaChart(
            Constants.GrafanaReleaseName,
            Constants.ObservabilityNamespace);

        await Task.WhenAll(
            prometheusTask,
            lokiTask,
            tempoTask,
            grafanaDashboardsTask,
            grafanaChartTask);

        this.logger.LogInformation($"Prometheus, Loki, Tempo, and Grafana installations succeeded.");

        this.logger.LogInformation(
            $"Waiting for prometheus to become ready on: {clClusterName}");
        progressReporter.Report("Waiting for prometheus to become ready...");
        await kubectlClient.WaitForPrometheusUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Prometheus is ready on: {clClusterName}");

        this.logger.LogInformation(
            $"Waiting for loki to become ready on: {clClusterName}");
        progressReporter.Report("Waiting for loki to become ready...");
        await kubectlClient.WaitForLokiUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Loki is ready on: {clClusterName}");

        this.logger.LogInformation(
            $"Waiting for tempo to become ready on: {clClusterName}");
        progressReporter.Report("Waiting for tempo to become ready...");
        await kubectlClient.WaitForTempoUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Tempo is ready on: {clClusterName}");

        this.logger.LogInformation(
            $"Waiting for grafana to become ready on: {clClusterName}");
        progressReporter.Report("Waiting for grafana to become ready...");
        await kubectlClient.WaitForGrafanaUp(Constants.ObservabilityNamespace);
        this.logger.LogInformation(
            $"Grafana is ready on: {clClusterName}");
    }

    private async Task EnableAnalyticsWorkloadAsync(
        string clClusterName,
        AnalyticsWorkloadProfileInput input,
        string ns,
        IProgress<string> progressReporter)
    {
        string kindClusterName = this.ToKindClusterName(clClusterName);

        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        this.logger.LogInformation($"Creating namespace {ns}.");
        await kubectlClient.CreateNamespaceAsync(ns);
        this.logger.LogInformation($"Namespace created.");

        this.logger.LogInformation($"Setting up spark operator service account rbac for {ns}.");
        await kubectlClient.CreateSparkOperatorServiceAccountRbac(ns);
        this.logger.LogInformation($"Spark operator service account rbac setup complete.");

        var telemetryCollectionEnabled =
            input.TelemetryProfile != null && input.TelemetryProfile.CollectionEnabled;
        progressReporter.Report("Installing cleanroom-spark-frontend...");
        this.logger.LogInformation(
            $"Starting installation of cleanroom-spark-frontend helm chart on: " +
            $"{kindClusterName}");
        await this.InstallSparkFrontendOnKindAsync(
            telemetryCollectionEnabled,
            kubeConfigFile);
        this.logger.LogInformation($"Cleanroom-spark-frontend helm chart installation " +
            $"succeeded.");

        progressReporter.Report("Installing cleanroom-spark-analytics-agent...");
        this.logger.LogInformation(
            $"Starting installation of cleanroom-spark-analytics-agent helm chart on: " +
            $"{kindClusterName}");
        var deploymentTemplate = await this.httpClientManager.GetDeploymentTemplate(
            this.logger,
            input.ConfigurationUrl!,
            input.ConfigurationUrlCaCert);
        await this.InstallAnalyticsAgentOnKindAsync(
            deploymentTemplate,
            telemetryCollectionEnabled,
            kubeConfigFile);
        this.logger.LogInformation($"Cleanroom-spark-analytics-agent helm chart installation " +
            $"succeeded.");

        progressReporter.Report("Waiting for cleanroom-spark-frontend to become ready...");
        this.logger.LogInformation(
            $"Waiting for spark frontend agent pod/deployment to become ready.");
        await kubectlClient.WaitForSparkFrontendUp(Constants.SparkFrontendServiceNamespace);
        this.logger.LogInformation($"Spark frontend pod/deployment are reporting ready.");

        progressReporter.Report("Waiting for cleanroom-spark-analytics-agent to become ready...");
        this.logger.LogInformation($"Waiting for analytics agent pod/deployment to become ready.");
        await kubectlClient.WaitForAnalyticsAgentUp(Constants.AnalyticsAgentNamespace);
        this.logger.LogInformation($"Analytics agent pod/deployment are reporting ready.");
    }

    private async Task InstallAnalyticsAgentOnKindAsync(
        DeploymentTemplate deploymentTemplate,
        bool telemetryCollectionEnabled,
        string kubeConfigFile)
    {
        string ns = Constants.AnalyticsAgentNamespace;
        string ccrFqdn = $"cleanroom-spark-analytics-agent.{ns}.svc";
        var valuesOverrideFiles = await GenerateValuesOverrideFiles();
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallAnalyticsAgentChart(
            Constants.AnalyticsAgentReleaseName,
            ns,
            valuesOverrideFiles);

        async Task<List<string>> GenerateValuesOverrideFiles()
        {
            List<string> files = [];
            var values = deploymentTemplate.Values;
            var app = await File.ReadAllTextAsync(
                "spark-analytics-agent/values.app.yaml");
            string caType = !string.IsNullOrEmpty(values.CcrgovEndpoint) ? "cgs" : "local";
            var discovery = values.CcrgovServiceCertDiscovery ?? new ServiceCertDiscoveryInput();
            app = app.Replace("<CA_TYPE>", caType);
            app = app.Replace("<CCR_FQDN>", ccrFqdn);
            app = app.Replace("<CCR_GOV_ENDPOINT>", values.CcrgovEndpoint);
            app = app.Replace("<CCR_GOV_API_PATH_PREFIX>", values.CcrgovApiPathPrefix);
            app = app.Replace("<CCR_GOV_SERVICE_CERT>", values.CcrgovServiceCert);
            app = app.Replace("<CCR_GOV_SERVICE_CERT_DISCOVERY_ENDPOINT>", discovery.Endpoint);
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_SNP_HOST_DATA>",
                discovery.SnpHostData);
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_SKIP_DIGEST_CHECK>",
                discovery.SkipDigestCheck.ToString().ToLower());
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_CONSTITUTION_DIGEST>",
                discovery.ConstitutionDigest);
            app = app.Replace(
                "<CCR_GOV_SERVICE_CERT_DISCOVERY_JSAPP_BUNDLE_DIGEST>",
                discovery.JsappBundleDigest);
            app = app.Replace("<SPARK_FRONTEND_ENDPOINT>", values.SparkFrontendEndpoint);
            app = app.Replace("<SPARK_FRONTEND_SNP_HOST_DATA>", values.SparkFrontendSnpHostData);
            app = app.Replace("<CCF_NETWORK_RECOVERY_MEMBERS>", values.CcfNetworkRecoveryMembers);
            app = app.Replace(
                "<ANALYTICS_AGENT_IMAGE_URL>",
                $"{ImageUtils.AnalyticsAgentImage()}:{ImageUtils.AnalyticsAgentTag()}");
            app = app.Replace(
                "<CCR_PROXY_IMAGE_URL>",
                $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}");
            app = app.Replace(
                "<CCR_SKR_IMAGE_URL>",
                $"{ImageUtils.SkrImage()}:{ImageUtils.SkrTag()}");
            app = app.Replace(
                "<CCR_ATTESTATION_IMAGE_URL>",
                $"{ImageUtils.CcrAttestationImage()}:{ImageUtils.CcrAttestationTag()}");
            app = app.Replace(
                "<OTEL_COLLECTOR_IMAGE_URL>",
                $"{ImageUtils.OtelCollectorImage()}:{ImageUtils.OtelCollectorTag()}");
            app = app.Replace(
                "<CCR_GOVERNANCE_IMAGE_URL>",
                $"{ImageUtils.CcrGovernanceImage()}:{ImageUtils.CcrGovernanceTag()}");
            var telemetryReplacements = new Dictionary<string, string>();
            if (telemetryCollectionEnabled)
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "true";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] =
                    $"{Constants.PrometheusServiceEndpoint}.cluster.local:9090/api/v1/write";
                telemetryReplacements["<LOKI_ENDPOINT>"] =
                    $"{Constants.LokiServiceEndpoint}.cluster.local:3100/otlp";
                telemetryReplacements["<TEMPO_ENDPOINT>"] =
                    $"{Constants.TempoServiceEndpoint}.cluster.local:4317";
            }
            else
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "false";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<LOKI_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<TEMPO_ENDPOINT>"] = string.Empty;
            }

            foreach (var kvp in telemetryReplacements)
            {
                app = app.Replace(kvp.Key, kvp.Value);
            }

            var valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, app);
            files.Add(valuesOverridesFile);

            var virt = await File.ReadAllTextAsync(
                "spark-analytics-agent/values.virtual.yaml");
            valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, virt);
            files.Add(valuesOverridesFile);
            return files;
        }
    }

    private async Task InstallSparkFrontendOnKindAsync(
        bool telemetryCollectionEnabled,
        string kubeConfigFile)
    {
        string ns = Constants.SparkFrontendServiceNamespace;
        var valuesOverrideFiles = await GenerateValuesOverrideFiles();
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallSparkFrontendChart(
            Constants.SparkFrontendReleaseName,
            ns,
            valuesOverrideFiles);

        async Task<List<string>> GenerateValuesOverrideFiles()
        {
            List<string> files = [];
            var app = await File.ReadAllTextAsync(
                "spark-frontend/values.app.yaml");
            app = app.Replace(
                "<SPARK_FRONTEND_IMAGE_URL>",
                $"{ImageUtils.SparkFrontendImage()}:{ImageUtils.SparkFrontendTag()}");
            app = app.Replace(
                "<CCR_PROXY_IMAGE_URL>",
                $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}");
            app = app.Replace(
                "<CCR_ATTESTATION_IMAGE_URL>",
                $"{ImageUtils.CcrAttestationImage()}:{ImageUtils.CcrAttestationTag()}");
            app = app.Replace(
                "<OTEL_COLLECTOR_IMAGE_URL>",
                $"{ImageUtils.OtelCollectorImage()}:{ImageUtils.OtelCollectorTag()}");
            app = app.Replace("<CLEANROOM_REGISTRY_URL>", $"{ImageUtils.RegistryUrl()}");
            app = app.Replace(
                "<CLEANROOM_VERSIONS_DOCUMENT>",
                $"{ImageUtils.GetCleanroomVersionsDocumentUrl()}");
            app = app.Replace(
                "<CLEANROOM_ANALYTICS_IMAGE_URL>",
                $"{ImageUtils.CleanroomAnalyticsApp()}");
            app = app.Replace(
                "<CLEANROOM_ANALYTICS_IMAGE_POLICY_DOCUMENT_URL>",
                $"{ImageUtils.CleanroomAnalyticsAppPolicyDocument()}");
            app = app.Replace("<ANALYTICS_NAMESPACE>", Constants.AnalyticsWorkloadNamespace);
            app = app.Replace("<ALLOW_ALL>", "true");
            app = app.Replace("<DEBUG_MODE>", "false");
            app = app.Replace(
                "<CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL>",
                $"{ImageUtils.SidecarsPolicyDocumentRegistryUrl()}");

            var telemetryReplacements = new Dictionary<string, string>();
            if (telemetryCollectionEnabled)
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "true";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] =
                    $"{Constants.PrometheusServiceEndpoint}.cluster.local:9090/api/v1/write";
                telemetryReplacements["<LOKI_ENDPOINT>"] =
                    $"{Constants.LokiServiceEndpoint}.cluster.local:3100/otlp";
                telemetryReplacements["<TEMPO_ENDPOINT>"] =
                    $"{Constants.TempoServiceEndpoint}.cluster.local:4317";
            }
            else
            {
                telemetryReplacements["<TELEMETRY_COLLECTION_ENABLED>"] = "false";
                telemetryReplacements["<PROMETHEUS_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<LOKI_ENDPOINT>"] = string.Empty;
                telemetryReplacements["<TEMPO_ENDPOINT>"] = string.Empty;
            }

            foreach (var kvp in telemetryReplacements)
            {
                app = app.Replace(kvp.Key, kvp.Value);
            }

            var valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, app);
            files.Add(valuesOverridesFile);

            var virt = await File.ReadAllTextAsync(
                "spark-frontend/values.virtual.yaml");
            virt = virt.Replace("<CLEANROOM_REGISTRY_USE_HTTP>", $"{ImageUtils.RegistryUseHttp()}");
            valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, virt);
            files.Add(valuesOverridesFile);
            return files;
        }
    }

    private async Task<ContainerListResponse> GetContainerById(string containerId)
    {
        var containers = await this.dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    {
                        "id", new Dictionary<string, bool>
                        {
                            { $"{containerId}", true }
                        }
                    }
                }
            });

        if (containers.Count != 1)
        {
            throw new Exception(
                $"Expecting 1 container with ID {containerId} but found {containers.Count}.");
        }

        return containers[0];
    }

    private async Task<ContainerListResponse> GetContainerByName(string containerName)
    {
        var containers = await this.dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    {
                        "name", new Dictionary<string, bool>
                        {
                            { $"^{containerName}$", true }
                        }
                    }
                }
            });

        if (containers.Count != 1)
        {
            throw new Exception(
                $"Expecting 1 container with name {containerName} but found {containers.Count}" +
                $". Details: {JsonSerializer.Serialize(containers, Utils.Options)}");
        }

        return containers[0];
    }

    private async Task<ContainerListResponse?> TryGetContainerByName(string containerName)
    {
        var containers = await this.dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    {
                        "name", new Dictionary<string, bool>
                        {
                            { $"^{containerName}$", true }
                        }
                    }
                }
            });

        if (containers.Count > 1)
        {
            throw new Exception(
                $"Expecting 1 container with name {containerName} but found {containers.Count}" +
                $". Details: {JsonSerializer.Serialize(containers, Utils.Options)}");
        }

        return containers.FirstOrDefault();
    }
}