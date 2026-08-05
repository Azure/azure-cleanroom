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
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
        string outDir = Path.GetTempPath();
        var kubeConfigFile = Path.Combine(outDir, $"{kindClusterName}.config");
        progressReporter.Report("Creating kind cluster...");
        this.logger.LogInformation($"Starting kind cluster creation: {kindClusterName}");
        int flexNodeCount =
            input.FlexNodeProfile is { Enabled: true, Mode: FlexNodeProfileInput.ModeManual }
            ? input.FlexNodeProfile.NodeCount
            : 0;
        try
        {
            await kindClient.CreateCluster(kindClusterName, flexNodeCount);
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

        if (withInternalAddress)
        {
            if (IsRunningInKubernetes())
            {
                // Running as a K8s pod. HOSTNAME is the pod name, not a Docker
                // container ID, so GetContainerById would fail. Instead, rewrite
                // the kubeconfig to use the control-plane container's IP on the
                // "kind" Docker network which is routable from the pod.
                kubeConfig = await this.RewriteKubeConfigForK8sAsync(kubeConfig, kindClusterName);
                await File.WriteAllTextAsync(kubeConfigFile, kubeConfig);
            }
            else
            {
                // Connect the kind control plane container to the docker network of this container
                // so that the api server via the internal address is reachable from this container.
                var containerId = Environment.GetEnvironmentVariable("HOSTNAME")!;
                var container = await this.GetContainerById(containerId);
                var networkName =
                    container.NetworkSettings.Networks.Single().Key;
                var kindContainerNames =
                    await kindClient.GetNodeNames(kindClusterName);
                foreach (var kindContainerName in kindContainerNames)
                {
                    var kindContainer =
                        await this.GetContainerByName(kindContainerName);
                    if (!kindContainer.NetworkSettings.Networks
                        .Keys.Contains(networkName))
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
        }

        // Connect the kind cluster network with the ccr-container registry container
        // so that kind is able to pull images from "localhost:5000".
        await ConnectKindToLocalContainerRegistry();

        if (input.ObservabilityProfile != null && input.ObservabilityProfile.Enabled)
        {
            this.logger.LogInformation($"Enabling telemetry for cluster: {clClusterName}");
            await this.EnableClusterObservabilityAsync(
                clClusterName,
                providerConfig,
                progressReporter);
        }

        if (input.MonitoringProfile != null && input.MonitoringProfile.Enabled)
        {
            this.logger.LogInformation($"Enabling monitoring for cluster: {clClusterName}");
            await this.EnableClusterMonitoringAsync(
                clClusterName,
                providerConfig,
                progressReporter);
        }

        if (input.AnalyticsWorkloadProfile != null && input.AnalyticsWorkloadProfile.Enabled)
        {
            await this.EnableAnalyticsWorkloadAsync(
                clClusterName,
                input.AnalyticsWorkloadProfile,
                Constants.AnalyticsWorkloadNamespace,
                providerConfig,
                progressReporter);
        }

        if (input.InferencingWorkloadProfile?.KServeProfile != null &&
            input.InferencingWorkloadProfile.KServeProfile.Enabled)
        {
            await this.EnableKServeInferencingWorkloadAsync(
                clClusterName,
                input.InferencingWorkloadProfile.KServeProfile,
                Constants.KServeInferencingWorkloadNamespace,
                providerConfig,
                progressReporter);
        }

        if (input.FlexNodeProfile != null && input.FlexNodeProfile.Enabled)
        {
            await this.EnableFlexNodeAsync(
                clClusterName,
                input.FlexNodeProfile,
                providerConfig,
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
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
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
                providerConfig,
                progressReporter);
        }

        if (input.MonitoringProfile != null && input.MonitoringProfile.Enabled)
        {
            this.logger.LogInformation($"Enabling monitoring for cluster: {clClusterName}");
            await this.EnableClusterMonitoringAsync(
                clClusterName,
                providerConfig,
                progressReporter);
        }

        if (input.AnalyticsWorkloadProfile != null && input.AnalyticsWorkloadProfile.Enabled)
        {
            await this.EnableAnalyticsWorkloadAsync(
                clClusterName,
                input.AnalyticsWorkloadProfile,
                Constants.AnalyticsWorkloadNamespace,
                providerConfig,
                progressReporter);
        }

        if (input.InferencingWorkloadProfile?.KServeProfile != null &&
            input.InferencingWorkloadProfile.KServeProfile.Enabled)
        {
            await this.EnableKServeInferencingWorkloadAsync(
                clClusterName,
                input.InferencingWorkloadProfile.KServeProfile,
                Constants.KServeInferencingWorkloadNamespace,
                providerConfig,
                progressReporter);
        }

        if (input.FlexNodeProfile != null && input.FlexNodeProfile.Enabled)
        {
            await this.EnableFlexNodeAsync(
                clClusterName,
                input.FlexNodeProfile,
                providerConfig,
                progressReporter);
        }

        progressReporter.Report($"Cluster update completed.");
        this.logger.LogInformation($"Cluster update completed: {clClusterName}");
        return await this.GetCluster(clClusterName, providerConfig);
    }

    public async Task DeleteCluster(string clusterName, JsonObject? providerConfig)
    {
        string kindClusterName =
            this.ResolveKindClusterName(clusterName, providerConfig);
        var kindClient = new KindClient(this.logger, this.configuration);
        await kindClient.DeleteCluster(kindClusterName);
    }

    public async Task CreateFlexNode(
        string clusterName,
        string nodeName,
        string providerID,
        string policySigningCertPem,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName = this.ResolveKindClusterName(clusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        var dockerCliClient = new DockerCliClient(this.logger);
        var kindClient = new KindClient(this.logger, this.configuration);

        progressReporter.Report($"Adding worker node '{nodeName}' to kind cluster...");
        this.logger.LogInformation(
            $"CreateFlexNode: adding worker node '{nodeName}' to cluster '{kindClusterName}'.");

        await kindClient.AddWorkerNode(
            kindClusterName,
            nodeName,
            taint: "for-flex-node=true:NoSchedule",
            providerID: providerID);

        // Configure the node with flex node labels/taints and install proxies.
        var flexNodeProfile = new FlexNodeProfileInput
        {
            Insecure = true,
            PolicySigningCertPem = policySigningCertPem,
        };

        await this.ConfigureFlexNodeWorkerAsync(
            nodeName,
            $"{kindClusterName}-control-plane",
            flexNodeProfile,
            kubectlClient,
            dockerCliClient,
            progressReporter);

        progressReporter.Report($"Flex node '{nodeName}' provisioned successfully.");
        this.logger.LogInformation(
            $"CreateFlexNode: node '{nodeName}' is ready with providerID '{providerID}'.");
    }

    public async Task DeleteFlexNode(
        string clusterName,
        string nodeName,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName = this.ResolveKindClusterName(clusterName, providerConfig);
        var dockerCliClient = new DockerCliClient(this.logger);

        // Remove the Docker container backing the Kind worker node.
        // Karpenter handles draining and deleting the Node object from the API server.
        progressReporter.Report($"Removing container '{nodeName}'...");
        try
        {
            await dockerCliClient.RemoveContainer(nodeName);
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(
                ex,
                $"docker rm failed for container '{nodeName}'.");
        }

        progressReporter.Report($"Flex node '{nodeName}' deleted successfully.");
        this.logger.LogInformation(
            $"DeleteFlexNode: node '{nodeName}' removed from cluster '{kindClusterName}'.");
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
        JsonObject? providerConfig,
        KubeConfigAccessRole accessRole,
        bool @internal = false)
    {
        string kindClusterName = this.ResolveKindClusterName(clClusterName, providerConfig);
        var kindClient = new KindClient(this.logger, this.configuration);
        var kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);

        try
        {
            var kubeConfig = @internal
                ? await File.ReadAllTextAsync(kubeConfigFile)
                : await kindClient.GetKubeConfig(kindClusterName);

            var kubectlClient = new KubectlClient(
                this.logger,
                this.configuration,
                kubeConfigFile);

            switch (accessRole)
            {
                case KubeConfigAccessRole.Readonly:
                    await kubectlClient.CreateReadOnlyRoleAsync(
                        Constants.ReadonlyUserName);

                    var readonlyKubeConfig = await Utils.GetUserKubeConfigAsync(
                        kubectlClient,
                        kubeConfig,
                        Constants.ReadonlyUserName);

                    return new CleanRoomClusterKubeConfig
                    {
                        Kubeconfig = Encoding.UTF8.GetBytes(readonlyKubeConfig)
                    };

                case KubeConfigAccessRole.Diagnostic:
                    await kubectlClient.CreateDiagnosticRoleAsync(
                        Constants.DiagnosticUserName);

                    var diagnosticKubeConfig = await Utils.GetUserKubeConfigAsync(
                        kubectlClient,
                        kubeConfig,
                        Constants.DiagnosticUserName);
                    return new CleanRoomClusterKubeConfig
                    {
                        Kubeconfig = Encoding.UTF8.GetBytes(diagnosticKubeConfig)
                    };

                default:
                    return new CleanRoomClusterKubeConfig
                    {
                        Kubeconfig = Encoding.UTF8.GetBytes(kubeConfig)
                    };
            }
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("could not locate any control plane nodes for cluster " +
        $"named '{kindClusterName}'"))
        {
            return null;
        }
    }

    public async Task<CleanRoomClusterHealth?> TryGetClusterHealth(
        string clClusterName,
        JsonObject? providerConfig)
    {
        string kindClusterName = this.ResolveKindClusterName(clClusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);

        return await Utils.GetClusterHealth(kubectlClient);
    }

    public async Task<CleanRoomCluster?> TryGetCluster(
            string clClusterName,
            JsonObject? providerConfig)
    {
        string kindClusterName = this.ResolveKindClusterName(clClusterName, providerConfig);
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
        bool monitoringEnabled =
            await kubectlClient.IsKaitoInstalled(Constants.MonitoringNamespace);
        JsonObject? monitoringStatus = null;
        if (monitoringEnabled)
        {
            monitoringStatus = await kubectlClient.GetKaitoWorkspaceStatus(
                Constants.MonitoringNamespace);
        }

        (bool kserveInferencingWorkloadEnabled, string? kserveInferencingAgentEndpoint) =
            await kubectlClient.TryGetInferencingAgentEndpoint(
                Constants.KServeInferencingAgentNamespace);
        var flexNodeObjects = await kubectlClient.GetFlexNodesAsync();
        bool flexNodeEnabled = flexNodeObjects.Count > 0;
        var flexNodes = flexNodeObjects.Select(node => new FlexNode
        {
            K8sNodeDetails = node
        }).ToList();
        return this.ToCleanRoomCluster(
            clClusterName,
            kindClusterName,
            analyticsWorkloadEnabled,
            analyticsAgentEndpoint,
            observabilityEnabled,
            observabilityEndpoint,
            monitoringEnabled,
            monitoringStatus,
            kserveInferencingWorkloadEnabled,
            kserveInferencingAgentEndpoint,
            flexNodeEnabled,
            flexNodes);
    }

    public async Task<AnalyticsWorkloadGeneratedDeployment> GenerateAnalyticsWorkloadDeployment(
        GenerateAnalyticsWorkloadDeploymentInput input,
        JsonObject? providerConfig)
    {
        var policyOption = SecurityPolicyConfigInput.Convert(input.SecurityPolicy);
        var contractData = await this.httpClientManager.GetContractData(
            this.logger,
            input.ContractUrl,
            input.ContractUrlCaCert,
            input.ContractUrlHeaders);

        var telemetryCollectionEnabled =
            input.TelemetryProfile != null && input.TelemetryProfile.CollectionEnabled;
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
                Values = AnalyticsAgentChartValues.ToAgentChartValues(
                    contractData,
                    telemetryCollectionEnabled,
                    Constants.SparkFrontendEndpoint,
                    AciConstants.AllowAllPolicy.Digest)
            },
            GovernancePolicy = new()
            {
                Type = "add",
                PolicyType = "snp-caci",
                Claims = new()
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] = AciConstants.AllowAllPolicy.Digest
                }
            },
            CcePolicy = new()
            {
                Value = AciConstants.AllowAllPolicy.RegoBase64,
                DocumentUrl = ImageUtils.GetAnalyticsAgentSecurityPolicyDocumentUrl()
            }
        };
    }

    public async Task<KServeInferencingWorkloadGeneratedDeployment>
        GenerateKServeInferencingWorkloadDeployment(
        GenerateKServeInferencingWorkloadDeploymentInput input,
        JsonObject? providerConfig)
    {
        var contractData = await this.httpClientManager.GetContractData(
            this.logger,
            input.ContractUrl,
            input.ContractUrlCaCert,
            input.ContractUrlHeaders);

        var telemetryCollectionEnabled =
            input.TelemetryProfile != null && input.TelemetryProfile.CollectionEnabled;
        return new KServeInferencingWorkloadGeneratedDeployment
        {
            DeploymentTemplate = new()
            {
                ChartMetadata = new()
                {
                    Chart = ImageUtils.GetInferencingAgentChartPath(),
                    Version = ImageUtils.GetInferencingAgentChartVersion(),
                    Release = Constants.KServeInferencingAgentReleaseName,
                    Namespace = Constants.KServeInferencingAgentNamespace
                },
                Values = KServeInferencingAgentChartValues.ToAgentChartValues(
                    contractData,
                    telemetryCollectionEnabled,
                    Constants.KServeInferencingFrontendEndpoint,
                    AciConstants.AllowAllPolicy2.Digest)
            },
            GovernancePolicy = new()
            {
                Type = "add",
                PolicyType = "snp-caci",
                Claims = new()
                {
                    ["x-ms-sevsnpvm-is-debuggable"] = false,
                    ["x-ms-sevsnpvm-hostdata"] = AciConstants.AllowAllPolicy.Digest
                }
            },
            CcePolicy = new()
            {
                Value = AciConstants.AllowAllPolicy.RegoBase64,
                DocumentUrl = ImageUtils.GetKServeInferencingAgentSecurityPolicyDocumentUrl()
            }
        };
    }

    private static bool IsRunningInKubernetes()
    {
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));
    }

    private async Task<string> GetInternalKubeConfigFileAsync(string kindClusterName)
    {
        var kindClient = new KindClient(this.logger, this.configuration);
        bool withInternalAddress =
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
        var kubeConfig = await kindClient.GetKubeConfig(kindClusterName, withInternalAddress);
        if (withInternalAddress && IsRunningInKubernetes())
        {
            kubeConfig = await this.RewriteKubeConfigForK8sAsync(kubeConfig, kindClusterName);
        }

        string kubeConfigFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(kubeConfigFile, kubeConfig);
        return kubeConfigFile;
    }

    // Rewrites the kubeconfig server address from the control-plane container
    // hostname to its IP on the "kind" Docker network. This is needed when
    // running as a K8s pod where Docker container hostnames don't resolve.
    private async Task<string> RewriteKubeConfigForK8sAsync(
        string kubeConfig,
        string kindClusterName)
    {
        string cpContainerName = $"{kindClusterName}-control-plane";
        var cpContainer = await this.GetContainerByName(cpContainerName);
        if (!cpContainer.NetworkSettings.Networks.TryGetValue(
            "kind",
            out var kindNetwork))
        {
            throw new InvalidOperationException(
                $"Control-plane container '{cpContainerName}' is not " +
                $"connected to the 'kind' network.");
        }

        string cpIP = kindNetwork.IPAddress;
        return kubeConfig.Replace(
            $"{cpContainerName}:6443",
            $"{cpIP}:6443");
    }

    private CleanRoomCluster ToCleanRoomCluster(
        string clClusterName,
        string kindClusterName,
        bool analyticsWorkloadEnabled,
        string? analyticsAgentEndpoint,
        bool observabilityEnabled,
        string? observabilityEndpoint,
        bool monitoringEnabled,
        JsonObject? monitoringStatus,
        bool kserveInferencingWorkloadEnabled,
        string? kserveInferencingAgentEndpoint,
        bool flexNodeEnabled,
        List<FlexNode>? flexNodes = null)
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
            MonitoringProfile = new MonitoringProfile
            {
                Enabled = monitoringEnabled,
                KaitoProfile = monitoringEnabled ? new KaitoProfile
                {
                    ModelEndpoint = "http://workspace-llama-3point1-8b-instruct.kaito-workspace.svc",
                    Workspace = new KaitoWorkspace
                    {
                        Status = monitoringStatus
                    }
                }
                : null
            },
            AnalyticsWorkloadProfile = new AnalyticsWorkloadProfile
            {
                Enabled = analyticsWorkloadEnabled,
                Namespace = analyticsWorkloadEnabled ? Constants.AnalyticsWorkloadNamespace : null,
                Endpoint = analyticsAgentEndpoint
            },
            InferencingWorkloadProfile = new InferencingProfile
            {
                KServeProfile = new KServeInferencingProfile
                {
                    Enabled = kserveInferencingWorkloadEnabled,
                    Namespace = kserveInferencingWorkloadEnabled ?
                    Constants.KServeInferencingWorkloadNamespace : null,
                    Endpoint = kserveInferencingAgentEndpoint
                }
            },
            FlexNodeProfile = new FlexNodeProfile
            {
                Enabled = flexNodeEnabled,
                Nodes = flexNodes
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

    // Resolves the kind cluster name to use. If the caller supplied a
    // "kindClusterName" override in providerConfig, that name is used
    // as-is (allowing an existing kind cluster to be targeted). Otherwise
    // the name is derived from the clean room cluster name.
    private string ResolveKindClusterName(
        string clClusterName,
        JsonObject? providerConfig)
    {
        var overrideName =
            providerConfig?["kindClusterName"]?.ToString();
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            return overrideName;
        }

        return this.ToKindClusterName(clClusterName);
    }

    private async Task EnableFlexNodeAsync(
        string clClusterName,
        FlexNodeProfileInput flexNodeProfile,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        if (flexNodeProfile.Mode == FlexNodeProfileInput.ModeAuto)
        {
            await this.EnableFlexNodeAutoAsync(
                clClusterName, flexNodeProfile, providerConfig, progressReporter);
            return;
        }

        await this.EnableFlexNodeManualAsync(
            clClusterName, flexNodeProfile, providerConfig, progressReporter);
    }

    private async Task EnableFlexNodeAutoAsync(
        string clClusterName,
        FlexNodeProfileInput flexNodeProfile,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);

        // Step 1: Install the Helm chart (controllers only).
        progressReporter.Report("Installing karpenter-provider-accr...");
        this.logger.LogInformation(
            $"EnableFlexNodeAutoAsync: installing chart on '{kindClusterName}'.");

        var karpenterProviderValues = new Dictionary<string, object>
        {
            ["image"] = new Dictionary<string, string>
            {
                ["repository"] = ImageUtils.GetKarpenterProviderImage(),
                ["tag"] = ImageUtils.GetKarpenterProviderTag(),
            },
        };
        var providerClientValues = new Dictionary<string, object>
        {
            ["image"] = new Dictionary<string, string>
            {
                ["repository"] = ImageUtils.GetProviderClientImage(),
                ["tag"] = ImageUtils.GetProviderClientTag(),
            },
            ["env"] = GetProviderClientEnvVars(),
        };
        var valuesOverride = new Dictionary<string, object>
        {
            ["karpenterProvider"] = karpenterProviderValues,
            ["providerClient"] = providerClientValues,
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithQuotingNecessaryStrings()
            .Build();
        var valuesFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(valuesFile, serializer.Serialize(valuesOverride));

        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallKarpenterProviderChart(
            Constants.KarpenterProviderAccrReleaseName,
            Constants.KarpenterProviderAccrNamespace,
            valuesFile);

        this.logger.LogInformation("EnableFlexNodeAutoAsync: chart installed.");

        // Step 2: Apply NodePool + FlexNodeClass CRs separately.
        await ApplyKarpenterResourcesAsync();

        async Task ApplyKarpenterResourcesAsync()
        {
            this.logger.LogInformation(
                $"ApplyKarpenterResourcesAsync: applying CRs for '{clClusterName}'.");

            var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);

            // Apply FlexNodeClass.
            string policySigningCertPem = flexNodeProfile.PolicySigningCertPem ?? string.Empty;
            string policySigningCertValue = string.IsNullOrEmpty(policySigningCertPem)
                ? "\"\""
                : "|\n    " + policySigningCertPem.Replace("\n", "\n    ");

            var classTemplate = await File.ReadAllTextAsync("flexnode/flex-node-class.yaml");
            classTemplate = classTemplate.Replace("<CLUSTER_NAME>", clClusterName)
                .Replace("<INFRA_TYPE>", "virtual")
                .Replace("<POLICY_SIGNING_CERT_PEM>", policySigningCertValue);

            var classFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(classFile, classTemplate);
            await kubectlClient.ApplyAsync(classFile);

            // Apply NodePool.
            var poolTemplate = await File.ReadAllTextAsync("flexnode/node-pool.yaml");
            poolTemplate = poolTemplate.Replace("<CLUSTER_NAME>", clClusterName)
                .Replace("<CONSOLIDATION_POLICY>", "WhenEmpty")
                .Replace("<CONSOLIDATE_AFTER>", "5s").Replace("<LIMITS_CPU>", "64");

            var poolFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(poolFile, poolTemplate);
            await kubectlClient.ApplyAsync(poolFile);

            this.logger.LogInformation("ApplyKarpenterResourcesAsync: CRs applied.");
        }

        Dictionary<string, string> GetProviderClientEnvVars()
        {
            var env = new Dictionary<string, string>();
            foreach (var entry in Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>())
            {
                string key = entry.Key?.ToString() ?? string.Empty;
                if (key.StartsWith("CR_CLUSTER_PROVIDER_", StringComparison.Ordinal))
                {
                    env[key] = entry.Value?.ToString() ?? string.Empty;
                }
            }

            return env;
        }
    }

    private async Task EnableFlexNodeManualAsync(
        string clClusterName,
        FlexNodeProfileInput flexNodeProfile,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        var dockerCliClient = new DockerCliClient(this.logger);
        int desiredNodeCount = flexNodeProfile.NodeCount;

        // Nodes already labeled as flex nodes are fully configured.
        var configuredFlexNodes = await kubectlClient.GetFlexNodesAsync();
        int configuredCount = configuredFlexNodes.Count;

        int toConfigureCount = desiredNodeCount - configuredCount;
        if (toConfigureCount <= 0)
        {
            progressReporter.Report(
                $"All {desiredNodeCount} flex node(s) already configured.");
            this.logger.LogInformation(
                $"All {desiredNodeCount} flex node(s) already configured.");
            return;
        }

        List<string> nodesToConfigure;
        if (flexNodeProfile.RequirePreProvisionedKindNodes)
        {
            // Pre-provisioned path: flex nodes must exist in the kind
            // config template from cluster creation time.
            var unconfiguredNames = await kubectlClient.GetUnconfiguredFlexNodeNamesAsync();
            int availableFlexNodes = configuredCount + unconfiguredNames.Count;
            if (availableFlexNodes != 0 && desiredNodeCount > availableFlexNodes)
            {
                throw new InvalidOperationException(
                    $"Requested {desiredNodeCount} flex node(s) but the " +
                    $"cluster only has {availableFlexNodes} flex node " +
                    $"candidate(s). Kind doesn't support adding nodes " +
                    $"to a running cluster. Recreate the cluster with " +
                    $"the desired flex node count or disable " +
                    $"requirePreProvisionedKindNodes.");
            }

            // Use convention-based naming (worker4, worker5, ...) for
            // backward compatibility with pre-provisioned nodes.
            // Index 4 because kind-config.yaml has: control-plane,
            // worker, worker2, worker3(llama), then flex workers.
            const int FlexNodeWorkerStartIndex = 4;
            nodesToConfigure = new List<string>();
            for (int i = 0; i < desiredNodeCount; i++)
            {
                int workerIndex = FlexNodeWorkerStartIndex + i;
                string nodeName = $"{kindClusterName}-worker{workerIndex}";
                nodesToConfigure.Add(nodeName);
            }
        }
        else
        {
            // Default path: dynamically add fresh worker nodes to the
            // running cluster via kindscaler. Does not use any
            // pre-provisioned unconfigured nodes.
            nodesToConfigure = await this.AddWorkerNodesViaKindScalerAsync(
                kindClusterName,
                toConfigureCount,
                dockerCliClient,
                progressReporter);
        }

        progressReporter.Report(
            $"Configuring {nodesToConfigure.Count} flex node(s) on kind cluster...");
        this.logger.LogInformation(
            $"Configuring {nodesToConfigure.Count} flex node(s) on kind cluster...");

        var tasks = new List<Task>();
        foreach (string nodeName in nodesToConfigure)
        {
            tasks.Add(this.ConfigureFlexNodeWorkerAsync(
                nodeName,
                $"{kindClusterName}-control-plane",
                flexNodeProfile,
                kubectlClient,
                dockerCliClient,
                progressReporter));
        }

        await Task.WhenAll(tasks);

        progressReporter.Report(
            $"All {nodesToConfigure.Count} flex node(s) configured successfully.");
        this.logger.LogInformation(
            $"All {nodesToConfigure.Count} flex node(s) configured successfully.");
    }

    // Adds worker nodes to a running Kind cluster using the kindscaler
    // approach (docker run + kubeadm join). Returns the names of the
    // newly created node containers.
    private async Task<List<string>> AddWorkerNodesViaKindScalerAsync(
        string kindClusterName,
        int count,
        DockerCliClient dockerCliClient,
        IProgress<string> progressReporter)
    {
        var kindClient = new KindClient(this.logger, this.configuration);
        var existingNodes = await kindClient.GetNodeNames(kindClusterName);

        // Find the highest existing worker index.
        int highestWorkerIndex = 0;
        string prefix = $"{kindClusterName}-worker";
        foreach (string node in existingNodes)
        {
            if (!node.StartsWith(prefix))
            {
                continue;
            }

            string suffix = node[prefix.Length..];
            if (int.TryParse(suffix, out int idx) &&
                idx > highestWorkerIndex)
            {
                highestWorkerIndex = idx;
            }
        }

        var newNodeNames = new List<string>();
        for (int i = 0; i < count; i++)
        {
            int workerIndex = highestWorkerIndex + 1 + i;
            string nodeName = $"{kindClusterName}-worker{workerIndex}";
            progressReporter.Report(
                $"Adding worker node '{nodeName}' to kind cluster " +
                $"via kindscaler...");
            this.logger.LogInformation(
                $"Adding worker node '{nodeName}' to kind cluster " +
                $"via kindscaler...");
            await kindClient.AddWorkerNode(
                kindClusterName,
                nodeName,
                "for-flex-node=true:NoSchedule");
            newNodeNames.Add(nodeName);
        }

        return newNodeNames;
    }

    private async Task ConfigureFlexNodeWorkerAsync(
        string nodeName,
        string controlPlaneNodeName,
        FlexNodeProfileInput flexNodeProfile,
        KubectlClient kubectlClient,
        DockerCliClient dockerCliClient,
        IProgress<string> progressReporter)
    {
        const string StagingDir = "/opt/api-server-proxy-staging";
        const string ProxyListenAddr = "127.0.0.1:6444";

        if (await kubectlClient.IsFlexNodeReadyAsync(nodeName))
        {
            progressReporter.Report(
                $"Flex node setup already completed on worker node '{nodeName}'...");
            this.logger.LogInformation(
                $"Flex node '{nodeName}' already has ready label. Skipping configuration.");
            return;
        }

        this.logger.LogInformation($"Configuring worker node '{nodeName}' as flex node...");

        // Add taint and labels to the worker node.
        await kubectlClient.TaintNodeAsync(
            nodeName,
            "pod-policy=required:NoSchedule",
            overwrite: true);

        await kubectlClient.LabelNodeAsync(
            nodeName,
            "cleanroom.azure.com/flexnode=true",
            overwrite: true);

        await kubectlClient.LabelNodeAsync(
            nodeName,
            "pod-policy=required",
            overwrite: true);

        // Remove the placeholder taint that was added during kind cluster creation.
        this.logger.LogInformation($"Removing placeholder taint from node '{nodeName}'...");
        await kubectlClient.RemoveTaintNodeAsync(nodeName, "for-flex-node");

        this.logger.LogInformation(
            $"Worker node '{nodeName}' configured with flex node taint and labels.");

        var oras = new OrasClient(this.logger, this.configuration);

        // Pull both OCI packages upfront.
        string apiServerProxyPackageDir = await this.PullOciPackageAsync(
            oras,
            "api-server-proxy",
            ImageUtils.ApiServerProxyPackageUrl(),
            nodeName,
            progressReporter);
        string kubeletProxyPackageDir = await this.PullOciPackageAsync(
            oras,
            "kubelet-proxy",
            ImageUtils.KubeletProxyPackageUrl(),
            nodeName,
            progressReporter);

        // Install api-server-proxy first so it can patch node status requests
        // when kubelet restarts during kubelet-proxy installation.
        await this.CopyProxyFilesAsync(
            nodeName,
            "api-server-proxy",
            StagingDir,
            apiServerProxyPackageDir,
            dockerCliClient);

        this.logger.LogInformation("Running api-server-proxy uninstall.sh on worker node...");
        await dockerCliClient.Exec(nodeName, $"bash {StagingDir}/uninstall.sh");
        this.logger.LogInformation("api-server-proxy uninstall script completed.");

        // Copy signing certificate to node.
        this.logger.LogInformation("Copying signing certificate to node...");
        var tempSigningCertPath = Path.Combine(Path.GetTempPath(), $"{nodeName}-signing-cert.pem");
        await File.WriteAllTextAsync(
            tempSigningCertPath,
            flexNodeProfile.PolicySigningCertPem ?? string.Empty);
        await dockerCliClient.Copy(nodeName, tempSigningCertPath, $"{StagingDir}/signing-cert.pem");

        // Run api-server-proxy install.sh (image-prep: binary + unit +
        // stage kind/configure.sh) followed by configure.sh (boot: certs,
        // wire kubelet through the proxy, start service).
        this.logger.LogInformation("Running api-server-proxy install.sh on worker node...");
        await dockerCliClient.Exec(
            nodeName,
            $"bash {StagingDir}/install.sh " +
            $"--env kind " +
            $"--local-binary {StagingDir}/api-server-proxy " +
            $"--listen-addr {ProxyListenAddr}");
        this.logger.LogInformation("api-server-proxy install script completed.");

        this.logger.LogInformation("Running api-server-proxy configure.sh on worker node...");
        string insecureArg = flexNodeProfile.Insecure ? "--insecure" : string.Empty;
        await dockerCliClient.Exec(
            nodeName,
            $"bash {StagingDir}/configure.sh " +
            $"--signing-cert-file {StagingDir}/signing-cert.pem " +
            insecureArg);
        this.logger.LogInformation("api-server-proxy configure script completed.");
        await this.VerifyServiceDeploymentAsync(nodeName, "api-server-proxy", dockerCliClient);

        // Install kubelet-proxy.
        const string KubeletProxyStagingDir = "/opt/kubelet-proxy-staging";
        await this.CopyProxyFilesAsync(
            nodeName,
            "kubelet-proxy",
            KubeletProxyStagingDir,
            kubeletProxyPackageDir,
            dockerCliClient);

        this.logger.LogInformation("Running kubelet-proxy uninstall.sh on worker node...");
        await dockerCliClient.Exec(nodeName, $"bash {KubeletProxyStagingDir}/uninstall.sh");
        this.logger.LogInformation("kubelet-proxy uninstall script completed.");

        // Copy the appropriate API policy file to the node.
        string apiPolicyFileName = flexNodeProfile.Insecure
            ? "insecure-api-policy.json"
            : "default-api-policy.json";
        string apiPolicyFilePath = Path.Combine(
            AppContext.BaseDirectory,
            "flexnode",
            "kubelet-proxy",
            apiPolicyFileName);
        this.logger.LogInformation(
            $"Copying kubelet-proxy API policy ({apiPolicyFileName}) to node...");
        await dockerCliClient.Copy(
            nodeName,
            apiPolicyFilePath,
            $"{KubeletProxyStagingDir}/{apiPolicyFileName}");

        // Extract the real apiserver-kubelet-client cert/key from the
        // control-plane container. It is signed by the cluster CA, so kubelet
        // accepts the proxy's connection.
        this.logger.LogInformation(
            "Extracting apiserver-kubelet-client cert from control-plane...");
        string tempCrt = Path.Combine(
            Path.GetTempPath(), $"{nodeName}-apiserver-kubelet-client.crt");
        string tempKey = Path.Combine(
            Path.GetTempPath(), $"{nodeName}-apiserver-kubelet-client.key");
        await dockerCliClient.CopyFromContainer(
            controlPlaneNodeName,
            "/etc/kubernetes/pki/apiserver-kubelet-client.crt",
            tempCrt);
        await dockerCliClient.CopyFromContainer(
            controlPlaneNodeName,
            "/etc/kubernetes/pki/apiserver-kubelet-client.key",
            tempKey);
        await dockerCliClient.Copy(
            nodeName, tempCrt, $"{KubeletProxyStagingDir}/client.crt");
        await dockerCliClient.Copy(
            nodeName, tempKey, $"{KubeletProxyStagingDir}/client.key");
        File.Delete(tempCrt);
        File.Delete(tempKey);

        // Run kubelet-proxy install.sh (image-prep: binary + unit + stage
        // kind/configure.sh) followed by configure.sh (boot: server cert,
        // install client cert + policy, move kubelet to :10251, start service).
        this.logger.LogInformation("Running kubelet-proxy install.sh on worker node...");
        await dockerCliClient.Exec(
            nodeName,
            $"bash {KubeletProxyStagingDir}/install.sh " +
            $"--env kind " +
            $"--local-binary {KubeletProxyStagingDir}/kubelet-proxy");
        this.logger.LogInformation("kubelet-proxy install script completed.");

        this.logger.LogInformation("Running kubelet-proxy configure.sh on worker node...");
        await dockerCliClient.Exec(
            nodeName,
            $"bash {KubeletProxyStagingDir}/configure.sh " +
            $"--client-cert {KubeletProxyStagingDir}/client.crt " +
            $"--client-key {KubeletProxyStagingDir}/client.key " +
            $"--api-policy-file {KubeletProxyStagingDir}/{apiPolicyFileName}");
        this.logger.LogInformation("kubelet-proxy configure script completed.");
        await this.VerifyServiceDeploymentAsync(nodeName, "kubelet-proxy", dockerCliClient);

        await kubectlClient.LabelNodeAsync(
            nodeName,
            "cleanroom.azure.com/ready=true",
            overwrite: true);
        this.logger.LogInformation(
            $"kubelet-proxy and api-server-proxy installed successfully on node: {nodeName}");
    }

    // PullOciPackageAsync pulls an OCI package from the registry into a local directory.
    private async Task<string> PullOciPackageAsync(
        OrasClient oras,
        string packageName,
        string packageUrl,
        string nodeName,
        IProgress<string> progressReporter)
    {
        progressReporter.Report($"Pulling {packageName} package...");
        this.logger.LogInformation($"Pulling {packageName} package from {packageUrl}...");

        string packageDir = Path.Combine(Path.GetTempPath(), $"{nodeName}-{packageName}-pkg");
        if (Directory.Exists(packageDir))
        {
            Directory.Delete(packageDir, recursive: true);
        }

        Directory.CreateDirectory(packageDir);
        bool useHttp = packageUrl.StartsWith("host.docker.internal") ||
            packageUrl.StartsWith("localhost") ||
            packageUrl.StartsWith("172.17.0.1") ||
            packageUrl.StartsWith("ccr-registry");
        await oras.Pull(packageUrl, packageDir, useHttp);
        this.logger.LogInformation($"{packageName} package pulled successfully.");
        return packageDir;
    }

    // CopyProxyFilesAsync creates the staging directory and copies
    // install.sh, uninstall.sh, and the binary to the node.
    private async Task CopyProxyFilesAsync(
        string nodeName,
        string proxyName,
        string stagingDir,
        string packageDir,
        DockerCliClient dockerCliClient)
    {
        this.logger.LogInformation($"Creating staging directory on node: {stagingDir}");
        await dockerCliClient.Exec(nodeName, $"mkdir -p {stagingDir}");
        await dockerCliClient.Exec(nodeName, $"mkdir -p {stagingDir}/kind");

        this.logger.LogInformation($"Copying {proxyName} files to node...");
        await dockerCliClient.Copy(
            nodeName,
            Path.Combine(packageDir, "uninstall.sh"),
            $"{stagingDir}/uninstall.sh");
        await dockerCliClient.Copy(
            nodeName,
            Path.Combine(packageDir, "install.sh"),
            $"{stagingDir}/install.sh");
        await dockerCliClient.Copy(
            nodeName,
            Path.Combine(packageDir, proxyName),
            $"{stagingDir}/{proxyName}");

        // Kind-specific configure.sh. install.sh --env kind stages it as
        // configure.sh, so it must be present under the kind/ subdir.
        await dockerCliClient.Copy(
            nodeName,
            Path.Combine(packageDir, "kind", "configure.sh"),
            $"{stagingDir}/kind/configure.sh");

        this.logger.LogInformation($"Verifying {proxyName} files on node...");
        await dockerCliClient.Exec(nodeName, $"ls -la {stagingDir}/");
    }

    private async Task VerifyServiceDeploymentAsync(
        string nodeName,
        string serviceName,
        DockerCliClient dockerCliClient)
    {
        this.logger.LogInformation($"Verifying {serviceName} deployment...");

        // Check service status.
        try
        {
            await dockerCliClient.Exec(
                nodeName,
                $"systemctl status {serviceName} --no-pager");
            this.logger.LogInformation($"{serviceName} service is running.");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning($"{serviceName} status check failed: {ex.Message}");
        }

        // Check recent service logs.
        try
        {
            await dockerCliClient.Exec(
                nodeName,
                $"journalctl -u {serviceName} --no-pager -n 20");
        }
        catch (Exception ex)
        {
            this.logger.LogWarning($"Failed to get {serviceName} logs: {ex.Message}");
        }
    }

    private async Task EnableClusterObservabilityAsync(
        string clClusterName,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);

        this.logger.LogInformation($"Creating namespace {Constants.ObservabilityNamespace}.");
        await kubectlClient.CreateNamespaceAsync(Constants.ObservabilityNamespace);
        this.logger.LogInformation($"Namespace created.");
        progressReporter.Report("Installing prometheus, loki, tempo and grafana...");
        this.logger.LogInformation(
            $"Installing Prometheus, Loki, Tempo and Grafana on: {clClusterName}.");

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

    private async Task EnableClusterMonitoringAsync(
        string clClusterName,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);
        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);

        this.logger.LogInformation($"Creating namespace {Constants.MonitoringNamespace}.");
        await kubectlClient.CreateNamespaceAsync(Constants.MonitoringNamespace);
        this.logger.LogInformation($"Namespace created.");
        progressReporter.Report("Installing Kaito workspace controller...");

        await helmClient.InstallKaitoChart(
            Constants.KaitoReleaseName,
            Constants.MonitoringNamespace);

        this.logger.LogInformation($"Kaito workspace controller installation succeeded.");

        this.logger.LogInformation(
            $"Waiting for Kaito workspace controller to become ready on: {clClusterName}");
        progressReporter.Report("Waiting for Kaito workspace controller to become ready...");
        await kubectlClient.WaitForKaitoUp(Constants.MonitoringNamespace);
        this.logger.LogInformation(
            $"Kaito workspace controller is ready on: {clClusterName}");

        string preferredNodeName = await kubectlClient.GetNodeNameByLabelAsync(
            "apps=llama-3point1-8b-instruct");

        // Check if image already exists on the node
        var dockerCliClient = new DockerCliClient(this.logger);
        var image = "ghcr.io/kaito-project/aikit/llama3.1:8b-instruct";
        var imageCheckCommand = $"crictl images -o json";
        this.logger.LogInformation(
            $"Checking if image {image} is already " +
            $"present on {preferredNodeName}.");
        var (exitCode, output, _) = await dockerCliClient.ExecWithOutput(
            preferredNodeName,
            imageCheckCommand);
        this.logger.LogInformation($"exitCode {exitCode} output {output}.");
        if (exitCode == 0 && output.Contains(image))
        {
            this.logger.LogInformation(
                $"Image {image} already present on {preferredNodeName}. Skipping image load.");
            progressReporter.Report(
                $"Image {image} already present on node {preferredNodeName}...");
        }
        else
        {
            var sharedDir = Environment.GetEnvironmentVariable("SHARED_DIR") ??
                throw new ArgumentNullException("SHARED_DIR");
            var imageFile = Path.Combine(sharedDir, "images/llama3.1:8b-instruct.tar");
            this.logger.LogInformation($"Loading {imageFile} into {preferredNodeName}.");
            progressReporter.Report(
                $"Loading {imageFile} into node {preferredNodeName} to avoid image pull " +
                $"during pod start...");
            var kindClient = new KindClient(this.logger, this.configuration);
            await kindClient.LoadImageArchive(imageFile, kindClusterName, preferredNodeName);
        }

        this.logger.LogInformation(
            $"Starting llama3.1:8b-instruct ai kit model deployment via Kaito on node " +
            $"{preferredNodeName}");
        progressReporter.Report(
            $"Starting llama3.1:8b-instruct ai kit model deployment via Kaito on node " +
            $"{preferredNodeName}...");
        await kubectlClient.DeployAIKitModelVirtual(
            Constants.MonitoringNamespace,
            preferredNodeName);
        this.logger.LogInformation(
            $"Llama3.1:8b-instruct ai kit model deployment started via Kaito on: {clClusterName}.");
    }

    private async Task EnableAnalyticsWorkloadAsync(
        string clClusterName,
        AnalyticsWorkloadProfileInput input,
        string ns,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);

        progressReporter.Report("Installing spark-operator...");
        this.logger.LogInformation(
            $"Starting installation of Spark-Operator helm chart on: {kindClusterName}");
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallSparkOperatorChart("spark-operator");
        this.logger.LogInformation($"Spark-Operator helm chart installation succeeded.");

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
        var deploymentTemplate =
            await this.httpClientManager.GetDeploymentTemplate<AnalyticsDeploymentTemplate>(
                this.logger,
                input.ConfigurationUrl!,
                input.ConfigurationUrlCaCert,
                input.ConfigurationUrlHeaders);
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

        progressReporter.Report("Waiting for spark-operator to become ready...");
        this.logger.LogInformation($"Waiting for Spark-Operator pod/deployment to become ready.");
        await kubectlClient.WaitForSparkOperatorUp("spark-operator");
        this.logger.LogInformation($"Spark-Operator pod/deployment are reporting ready.");
    }

    private async Task EnableKServeInferencingWorkloadAsync(
        string clClusterName,
        KServeInferencingWorkloadProfileInput input,
        string ns,
        JsonObject? providerConfig,
        IProgress<string> progressReporter)
    {
        string kindClusterName =
            this.ResolveKindClusterName(clClusterName, providerConfig);
        string kubeConfigFile = await this.GetInternalKubeConfigFileAsync(kindClusterName);

        progressReporter.Report("Installing cert-manager...");
        this.logger.LogInformation(
            $"Starting installation of cert-manager helm chart on: {kindClusterName}");
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallCertManagerChart("cert-manager");
        this.logger.LogInformation($"Cert-manager helm chart installation succeeded.");

        var kubectlClient = new KubectlClient(this.logger, this.configuration, kubeConfigFile);

        this.logger.LogInformation($"Creating namespace {ns}.");
        await kubectlClient.CreateNamespaceAsync(ns);
        this.logger.LogInformation($"Namespace created.");

        progressReporter.Report("Installing KServe...");
        this.logger.LogInformation(
            $"Starting installation of KServe helm chart on: {kindClusterName}");
        await helmClient.InstallKServe();
        this.logger.LogInformation($"KServe helm chart installation succeeded.");

        var deploymentTemplate =
            await this.httpClientManager.GetDeploymentTemplate<KServeInferencingDeploymentTemplate>(
                this.logger,
                input.ConfigurationUrl!,
                input.ConfigurationUrlCaCert,
                input.ConfigurationUrlHeaders);

        var telemetryCollectionEnabled =
            input.TelemetryProfile != null &&
            input.TelemetryProfile.CollectionEnabled;
        progressReporter.Report("Installing kserve-inferencing-frontend...");
        this.logger.LogInformation(
            $"Starting installation of kserve-inferencing-frontend helm chart on: " +
            $"{kindClusterName}");
        await this.InstallKServeInferencingFrontendOnKindAsync(
            deploymentTemplate,
            telemetryCollectionEnabled,
            kubeConfigFile);
        this.logger.LogInformation($"Kserve-inferencing-frontend helm chart installation " +
            $"succeeded.");

        progressReporter.Report("Installing kserve-inferencing-agent...");
        this.logger.LogInformation(
            $"Starting installation of kserve-inferencing-agent helm chart on: " +
            $"{kindClusterName}");
        await this.InstallKServeInferencingAgentOnKindAsync(
            deploymentTemplate,
            telemetryCollectionEnabled,
            kubeConfigFile);
        this.logger.LogInformation($"Kserve-inferencing-agent helm chart installation " +
            $"succeeded.");

        progressReporter.Report("Waiting for KServe to become ready...");
        this.logger.LogInformation($"Waiting for KServe pod/deployment to become ready.");
        await kubectlClient.WaitForKServeUp("kserve");
        this.logger.LogInformation($"KServe pod/deployment are reporting ready.");

        progressReporter.Report("Waiting for kserve-inferencing-frontend to become ready...");
        this.logger.LogInformation(
            $"Waiting for inferencing frontend pod/deployment to become ready.");
        await kubectlClient.WaitForInferencingFrontendUp(
            Constants.KServeInferencingFrontendServiceNamespace);
        this.logger.LogInformation($"Inferencing frontend pod/deployment are reporting ready.");

        progressReporter.Report("Waiting for kserve-inferencing-agent to become ready...");
        this.logger.LogInformation(
            $"Waiting for inferencing agent pod/deployment to become ready.");
        await kubectlClient.WaitForInferencingAgentUp(Constants.KServeInferencingAgentNamespace);
        this.logger.LogInformation($"Inferencing agent pod/deployment are reporting ready.");
    }

    private async Task InstallAnalyticsAgentOnKindAsync(
        AnalyticsDeploymentTemplate deploymentTemplate,
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
                $"{ImageUtils.LocalSkrImage()}:{ImageUtils.LocalSkrTag()}");
            app = app.Replace(
                "<OTEL_COLLECTOR_IMAGE_URL>",
                $"{ImageUtils.OtelCollectorImage()}:{ImageUtils.OtelCollectorTag()}");
            app = app.Replace(
                "<CCR_GOVERNANCE_IMAGE_URL>",
                $"{ImageUtils.CcrGovernanceVirtualImage()}:{ImageUtils.CcrGovernanceVirtualTag()}");
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
                "<CCR_SKR_IMAGE_URL>",
                $"{ImageUtils.LocalSkrImage()}:{ImageUtils.LocalSkrTag()}");
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

    private async Task InstallKServeInferencingAgentOnKindAsync(
        KServeInferencingDeploymentTemplate deploymentTemplate,
        bool telemetryCollectionEnabled,
        string kubeConfigFile)
    {
        string ns = Constants.KServeInferencingAgentNamespace;
        string ccrFqdn = $"kserve-inferencing-agent.{ns}.svc";
        var valuesOverrideFiles = await GenerateValuesOverrideFiles();
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallInferencingAgentChart(
            Constants.KServeInferencingAgentReleaseName,
            ns,
            valuesOverrideFiles);

        async Task<List<string>> GenerateValuesOverrideFiles()
        {
            List<string> files = [];
            var values = deploymentTemplate.Values;
            var app = await File.ReadAllTextAsync(
                "kserve-inferencing-agent/values.app.yaml");
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
            app = app.Replace(
                "<INFERENCING_FRONTEND_ENDPOINT>",
                values.InferencingFrontendEndpoint);
            app = app.Replace(
                "<INFERENCING_FRONTEND_SNP_HOST_DATA>",
                values.InferencingFrontendSnpHostData);
            app = app.Replace("<CCF_NETWORK_RECOVERY_MEMBERS>", values.CcfNetworkRecoveryMembers);
            app = app.Replace(
                "<INFERENCING_AGENT_IMAGE_URL>",
                $"{ImageUtils.InferencingAgentImage()}:{ImageUtils.InferencingAgentTag()}");
            app = app.Replace(
                "<CCR_PROXY_IMAGE_URL>",
                $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}");
            app = app.Replace(
                "<CCR_SKR_IMAGE_URL>",
                $"{ImageUtils.LocalSkrImage()}:{ImageUtils.LocalSkrTag()}");
            app = app.Replace(
                "<OTEL_COLLECTOR_IMAGE_URL>",
                $"{ImageUtils.OtelCollectorImage()}:{ImageUtils.OtelCollectorTag()}");
            app = app.Replace(
                "<CCR_GOVERNANCE_IMAGE_URL>",
                $"{ImageUtils.CcrGovernanceVirtualImage()}:{ImageUtils.CcrGovernanceVirtualTag()}");
            app = app.Replace("<OHTTP_ENABLED>", "true");
            app = app.Replace(
                "<OHTTP_GATEWAY_IMAGE_URL>",
                $"{ImageUtils.OhttpGatewayImage()}:{ImageUtils.OhttpGatewayTag()}");
            app = app.Replace(
                "<INFERENCING_NAMESPACE>",
                Constants.KServeInferencingWorkloadNamespace);

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
                "kserve-inferencing-agent/values.virtual.yaml");
            valuesOverridesFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(valuesOverridesFile, virt);
            files.Add(valuesOverridesFile);

            return files;
        }
    }

    private async Task InstallKServeInferencingFrontendOnKindAsync(
        KServeInferencingDeploymentTemplate deploymentTemplate,
        bool telemetryCollectionEnabled,
        string kubeConfigFile)
    {
        string ns = Constants.KServeInferencingFrontendServiceNamespace;
        var valuesOverrideFiles = await GenerateValuesOverrideFiles();
        var helmClient = new HelmClient(this.logger, this.configuration, kubeConfigFile);
        await helmClient.InstallInferencingFrontendChart(
            Constants.KServeInferencingFrontendReleaseName,
            ns,
            valuesOverrideFiles);

        async Task<List<string>> GenerateValuesOverrideFiles()
        {
            List<string> files = [];
            var values = deploymentTemplate.Values;
            var app = await File.ReadAllTextAsync(
                "kserve-inferencing-frontend/values.app.yaml");
            var discovery = values.CcrgovServiceCertDiscovery ?? new ServiceCertDiscoveryInput();
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
            app = app.Replace(
                "<INFERENCING_FRONTEND_IMAGE_URL>",
                $"{ImageUtils.InferencingFrontendImage()}:{ImageUtils.InferencingFrontendTag()}");
            app = app.Replace(
                "<CCR_PROXY_IMAGE_URL>",
                $"{ImageUtils.CcrProxyImage()}:{ImageUtils.CcrProxyTag()}");
            app = app.Replace(
                "<CCR_SKR_IMAGE_URL>",
                $"{ImageUtils.LocalSkrImage()}:{ImageUtils.LocalSkrTag()}");
            app = app.Replace(
                "<OTEL_COLLECTOR_IMAGE_URL>",
                $"{ImageUtils.OtelCollectorImage()}:{ImageUtils.OtelCollectorTag()}");
            app = app.Replace(
                "<CCR_GOVERNANCE_IMAGE_URL>",
                $"{ImageUtils.CcrGovernanceVirtualImage()}:{ImageUtils.CcrGovernanceVirtualTag()}");
            app = app.Replace("<CLEANROOM_REGISTRY_URL>", $"{ImageUtils.RegistryUrl()}");
            app = app.Replace(
                "<CLEANROOM_VERSIONS_DOCUMENT>",
                $"{ImageUtils.GetCleanroomVersionsDocumentUrl()}");
            app = app.Replace(
                "<CLEANROOM_CVM_MEASUREMENTS_DOCUMENT>",
                $"{ImageUtils.GetCleanroomCvmMeasurementsVirtualDocumentUrl()}");
            app = app.Replace(
                "<INFERENCING_DIGESTS_DOCUMENT>",
                $"{ImageUtils.GetInferencingDigestsDocumentUrl()}");
            app = app.Replace(
                "<CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL>",
                $"{ImageUtils.SidecarsPolicyDocumentRegistryUrl()}");
            app = app.Replace(
                "<INFERENCING_NAMESPACE>",
                Constants.KServeInferencingWorkloadNamespace);
            app = app.Replace("<ALLOW_ALL>", "true");
            app = app.Replace("<DEBUG_MODE>", "false");
            app = app.Replace(
                "<ENABLE_TEST_ENDPOINTS>",
                values.EnableTestEndpoints.ToString().ToLower());

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
                "kserve-inferencing-frontend/values.virtual.yaml");
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
