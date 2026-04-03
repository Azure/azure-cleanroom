// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CleanRoomProvider;

public class KubectlClient : RunCommand
{
    private const string KaitoWsName = "workspace-llama-3point1-8b-instruct";

    // It can take a while as containers start on caci and NFS storage provisioning completes.
    private static readonly TimeSpan CaciWaitTimeout = TimeSpan.FromMinutes(15);

    private readonly ILogger logger;
    private readonly IConfiguration config;
    private readonly string kubeConfigFile;

    public KubectlClient(
        ILogger logger,
        IConfiguration config,
        string kubeConfigFile)
        : base(logger)
    {
        this.logger = logger;
        this.config = config;
        this.kubeConfigFile = kubeConfigFile;
    }

    public async Task CreateNamespaceAsync(string ns)
    {
        try
        {
            await this.Kubectl($"create namespace {ns} --kubeconfig={this.kubeConfigFile}");
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("Error from server (AlreadyExists)"))
        {
            // Ignore.
        }
    }

    public async Task<bool> NamespaceExistsAsync(string ns)
    {
        try
        {
            await this.Kubectl(
                $"get namespace {ns} --kubeconfig={this.kubeConfigFile}",
                skipOutputLogging: true);
            return true;
        }
        catch (ExecuteCommandException e) when (e.Message.Contains("Error from server (NotFound)"))
        {
            return false;
        }
    }

    public async Task ApplyAsync(string file)
    {
        await this.Kubectl($"apply -f {file} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task CreateExternalDnsAzureConfigSecret(
        string ns,
        string tenantId,
        string subscriptionId,
        string resourceGroupName)
    {
        var template = await File.ReadAllTextAsync("external-dns/azure.json");
        template = template.Replace("<TENANT_ID>", tenantId);
        template = template.Replace("<SUBSCRIPTION_ID>", subscriptionId);
        template = template.Replace("<AZURE_DNS_ZONE_RESOURCE_GROUP>", resourceGroupName);
        var base64Config = Convert.ToBase64String(Encoding.UTF8.GetBytes(template));

        template = await File.ReadAllTextAsync("external-dns/secret.yaml");
        template = template.Replace("<NAMESPACE>", ns);
        template = template.Replace("<CONFIG>", base64Config);
        var secretConfig = Path.GetTempFileName();
        await File.WriteAllTextAsync(secretConfig, template);
        await this.Kubectl($"apply -f {secretConfig} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task CreateExternalDnsNamespace(string ns)
    {
        var template = await File.ReadAllTextAsync("external-dns/ns.yaml");
        template = template.Replace("<NAMESPACE>", ns);
        var secretConfig = Path.GetTempFileName();
        await File.WriteAllTextAsync(secretConfig, template);
        await this.Kubectl($"apply -f {secretConfig} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task CreateSparkOperatorServiceAccountRbac(string ns)
    {
        var template = await File.ReadAllTextAsync("spark-operator/spark-application-rbac.yaml");
        template = template.Replace("<NAMESPACE>", ns);
        var rbacConfig = Path.GetTempFileName();
        await File.WriteAllTextAsync(rbacConfig, template);
        await this.Kubectl($"apply -f {rbacConfig} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task WaitForSparkOperatorUp(string ns)
    {
        TimeSpan timeout = TimeSpan.FromMinutes(6);
        await this.KubectlWait(
            $"--for=condition=ready pod " +
            $"-l=app.kubernetes.io/name=spark-operator -n {ns} " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
        await this.KubectlWait(
            $" --for=condition=available deployment " +
            $"-l=app.kubernetes.io/name=spark-operator -n {ns} " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
    }

    public async Task WaitForKServeUp(string ns)
    {
        TimeSpan timeout = TimeSpan.FromMinutes(6);
        await this.KubectlWait(
            $"--for=condition=ready pod " +
            $"-l=control-plane=kserve-controller-manager -n {ns} " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
        await this.KubectlWait(
            $"--for=condition=available deployment " +
            $"-l=control-plane=kserve-controller-manager -n {ns} " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
    }

    public async Task WaitForAnalyticsAgentUp(string ns)
    {
        string name = "cleanroom-spark-analytics-agent";
        try
        {
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
            await this.KubectlWait(
                $"--for=condition=available deployment " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task<int> GetStatefulSetReplicaCountAsync(string ns, string name)
    {
        var output = await this.Kubectl(
            $"get statefulset {name} -n {ns} -o json --kubeconfig={this.kubeConfigFile}",
            skipOutputLogging: true);

        using var doc = JsonDocument.Parse(output);
        if (!doc.RootElement.TryGetProperty("spec", out var spec) ||
            !spec.TryGetProperty("replicas", out var replicasProperty))
        {
            throw new InvalidOperationException(
                $"Unable to determine replica count for statefulset {name} in namespace {ns}.");
        }

        return replicasProperty.GetInt32();
    }

    public async Task ScaleStatefulSetAsync(string ns, string name, int replicas)
    {
        var currentReplicas = await this.GetStatefulSetReplicaCountAsync(ns, name);
        await this.Kubectl(
            $"scale statefulset {name} --current-replicas={currentReplicas} --replicas={replicas} " +
            $"-n {ns} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task WaitForStatefulSetReadyAsync(string ns, string name, int replicas)
    {
        TimeSpan timeout = TimeSpan.FromMinutes(15);
        await this.KubectlRolloutStatus(
            $"statefulsets {name} -n {ns} " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
    }

    public async Task WaitForVN2NodeReady()
    {
        // Wait for the VN2 virtual node to register and become ready. The VN2 helm
        // chart installs a kubelet that registers as a node with the label
        // virtualization=virtualnode2. RBAC propagation delays can cause the VN2
        // proxycri container to crash-loop initially, delaying node registration.
        TimeSpan timeout = TimeSpan.FromMinutes(5);
        await this.KubectlWait(
            $"--for=condition=ready node " +
            $"-l virtualization=virtualnode2 " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
    }

    public async Task WaitForWorkloadIdentityDeploymentUp()
    {
        TimeSpan timeout = TimeSpan.FromMinutes(10);
        await this.KubectlWait(
            $"--for=condition=available deployment " +
            $"-l=azure-workload-identity.io/system=true -n kube-system " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
    }

    public async Task WaitForExternalDnsUp(string ns)
    {
        TimeSpan timeout = TimeSpan.FromMinutes(5);
        await this.KubectlWait(
            $"--for=condition=ready pod " +
            $"-l=app.kubernetes.io/name=external-dns -n {ns} " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
        await this.KubectlWait(
            $"--for=condition=available deployment " +
            $"-l=app.kubernetes.io/name=external-dns -n {ns} " +
            $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
            timeout);
    }

    public async Task WaitForSparkFrontendUp(string ns)
    {
        string name = "cleanroom-spark-frontend";
        try
        {
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
            await this.KubectlWait(
                $"--for=condition=available deployment " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task WaitForInferencingFrontendUp(string ns)
    {
        string name = "kserve-inferencing-frontend";
        try
        {
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
            await this.KubectlWait(
                $"--for=condition=available deployment " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task WaitForInferencingAgentUp(string ns)
    {
        string name = "kserve-inferencing-agent";
        try
        {
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
            await this.KubectlWait(
                $"--for=condition=available deployment " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task WaitForPrometheusUp(string ns)
    {
        string name = "cleanroom-spark-prometheus";
        try
        {
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/instance={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
            await this.KubectlWait(
                $"--for=condition=available deployment " +
                $"-l=app.kubernetes.io/instance={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task WaitForGrafanaUp(string ns)
    {
        string name = "cleanroom-spark-grafana";
        try
        {
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/instance={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
            await this.KubectlWait(
                $"--for=condition=available deployment " +
                $"-l=app.kubernetes.io/instance={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task WaitForKaitoUp(string ns)
    {
        string name = "workspace";
        try
        {
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
            await this.KubectlWait(
                $"--for=condition=available deployment " +
                $"-l=app.kubernetes.io/name={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task WaitForLokiUp(string ns)
    {
        string name = "cleanroom-spark-loki";
        try
        {
            // Loki deploys as a StatefulSet and not as a Deployment. Hence, we wait for the pods
            // to be available to deduce that the deployment is ready.
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/instance={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task WaitForTempoUp(string ns)
    {
        string name = "cleanroom-spark-tempo";
        try
        {
            // Tempo deploys as a StatefulSet and not as a Deployment. Hence, we wait for the pods
            // to be available to deduce that the deployment is ready.
            await this.KubectlWait(
                $"--for=condition=ready pod " +
                $"-l=app.kubernetes.io/instance={name} -n {ns} " +
                $"--kubeconfig={this.kubeConfigFile} --timeout=10s",
                CaciWaitTimeout);
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("timed out waiting for the condition"))
        {
            var events = await this.GetPodFailureEvents(ns, name);
            throw new ExecuteCommandException(
                e.Message + $" Warning events: {JsonSerializer.Serialize(events.Items)}",
                e);
        }
    }

    public async Task<(bool found, string? endpoint)> TryGetAnalyticsAgentEndpoint(string ns)
    {
        var output = await this.Kubectl(
            $"get svc " +
            $"-n {ns} -l=app.kubernetes.io/name=cleanroom-spark-analytics-agent " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: true);

        var services = JsonSerializer.Deserialize<K8sServices>(output)!;
        if (!services.Items.Any())
        {
            return (false, null);
        }

        if (services.Items.Count != 1)
        {
            throw new Exception($"Expecting only 1 service but found {services.Items.Count}.");
        }

        var service = services.Items[0];
        string? endpoint;
        if (service.Spec.Type == "ClusterIP")
        {
            endpoint = $"{service.Metadata.Name}.{service.Metadata.Namespace}.svc";
        }
        else if (service.Spec.Type == "LoadBalancer")
        {
            if (service.Metadata.Annotations != null &&
                service.Metadata.Annotations.TryGetValue(
                    Constants.ServiceFqdnAnnotation,
                    out string? fqdn))
            {
                endpoint = fqdn;
            }
            else
            {
                endpoint = service.Status.LoadBalancer.Ingress?.FirstOrDefault()?.IP;
            }
        }
        else
        {
            throw new Exception($"Unexpected service.spec.type value: '{service.Spec.Type}'.");
        }

        if (!string.IsNullOrEmpty(endpoint))
        {
            return (true, $"https://{endpoint}");
        }

        return (true, null);
    }

    public async Task<(bool found, string? endpoint)> TryGetObservabilityEndpoint(string ns)
    {
        var output = await this.Kubectl(
            $"get svc " +
            $"-n {ns} -l=app.kubernetes.io/name=grafana " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: true);

        var services = JsonSerializer.Deserialize<K8sServices>(output)!;
        if (!services.Items.Any())
        {
            return (false, null);
        }

        if (services.Items.Count != 1)
        {
            throw new Exception($"Expecting only 1 service but found {services.Items.Count}.");
        }

        var service = services.Items[0];
        string? endpoint;
        if (service.Spec.Type == "ClusterIP")
        {
            endpoint = $"{service.Metadata.Name}.{service.Metadata.Namespace}.svc";
        }
        else
        {
            throw new Exception($"Unexpected service.spec.type value: '{service.Spec.Type}'.");
        }

        if (!string.IsNullOrEmpty(endpoint))
        {
            return (true, $"http://{endpoint}");
        }

        return (true, null);
    }

    public async Task<bool> IsKaitoInstalled(string ns)
    {
        var output = await this.Kubectl(
            $"get svc " +
            $"-n {ns} -l=app.kubernetes.io/name=workspace " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: true);

        var services = JsonSerializer.Deserialize<K8sServices>(output)!;
        if (!services.Items.Any())
        {
            return false;
        }

        if (services.Items.Count != 1)
        {
            throw new Exception($"Expecting only 1 service but found {services.Items.Count}.");
        }

        return true;
    }

    public async Task<JsonObject?> GetKaitoWorkspaceStatus(string ns)
    {
        try
        {
            var output = await this.Kubectl(
                $"get workspace/{KaitoWsName} " +
                $"-n {ns} -o json " +
                $"--kubeconfig={this.kubeConfigFile}",
                skipOutputLogging: true);

            var workspace = JsonSerializer.Deserialize<JsonObject>(output);
            if (workspace != null &&
                workspace.TryGetPropertyValue("status", out var statusNode) &&
                statusNode != null)
            {
                return statusNode.AsObject();
            }

            return null;
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("Error from server (NotFound)"))
        {
            return null;
        }
    }

    public async Task DeployAIKitModelVirtual(string ns, string preferredNodeName)
    {
        var template = await File.ReadAllTextAsync("kaito/aikit-workspace.virtual.yaml");
        template = template.Replace("<NAME>", KaitoWsName);
        template = template.Replace("<NAMESPACE>", ns);
        template = template.Replace("<NODENAME>", preferredNodeName);
        var aikitWs = Path.GetTempFileName();
        await File.WriteAllTextAsync(aikitWs, template);
        await this.Kubectl($"apply -f {aikitWs} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task<(bool found, string? endpoint)> TryGetInferencingAgentEndpoint(string ns)
    {
        var output = await this.Kubectl(
            $"get svc " +
            $"-n {ns} -l=app.kubernetes.io/name=kserve-inferencing-agent " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: true);

        var services = JsonSerializer.Deserialize<K8sServices>(output)!;
        if (!services.Items.Any())
        {
            return (false, null);
        }

        if (services.Items.Count != 1)
        {
            throw new Exception($"Expecting only 1 service but found {services.Items.Count}.");
        }

        var service = services.Items[0];
        string? endpoint;
        if (service.Spec.Type == "ClusterIP")
        {
            endpoint = $"{service.Metadata.Name}.{service.Metadata.Namespace}.svc";
        }
        else if (service.Spec.Type == "LoadBalancer")
        {
            if (service.Metadata.Annotations != null &&
                service.Metadata.Annotations.TryGetValue(
                    Constants.ServiceFqdnAnnotation,
                    out string? fqdn))
            {
                endpoint = fqdn;
            }
            else
            {
                endpoint = service.Status.LoadBalancer.Ingress?.FirstOrDefault()?.IP;
            }
        }
        else
        {
            throw new Exception($"Unexpected service.spec.type value: '{service.Spec.Type}'.");
        }

        if (!string.IsNullOrEmpty(endpoint))
        {
            return (true, $"https://{endpoint}");
        }

        return (true, null);
    }

    public async Task InstallGrafanaDashboards(string ns)
    {
        var dashboardDir = "observability/grafana/dashboards";

        // Get all files in the dashboard directory
        var dashboardFiles = Directory.GetFiles(dashboardDir);
        foreach (var dashboardFile in dashboardFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(dashboardFile);

            // Sanitize the file name for k8s ConfigMap naming standards
            var configMapName = fileName
                .ToLowerInvariant()
                .Replace("_", "-")
                .Replace(".", "-");

            // Create ConfigMap for this specific file
            var output = await this.Kubectl(
                $"create configmap " +
                $"{configMapName} " +
                $"--from-file={dashboardFile} -n {ns} --kubeconfig {this.kubeConfigFile} " +
                $"--dry-run=client -o yaml",
                skipOutputLogging: true);
            var configMapFile = Path.GetTempFileName();
            await File.WriteAllTextAsync(configMapFile, output);
            await this.Kubectl($"apply -f {configMapFile} --kubeconfig {this.kubeConfigFile}");
            await this.Kubectl(
                $"label configmap {configMapName} " +
                $"grafana_dashboard=1 " +
                $"-n {ns} --kubeconfig {this.kubeConfigFile}");
        }
    }

    public async Task CreateReadOnlyRoleAsync(string userName)
    {
        var roleYaml = await File.ReadAllTextAsync(
            "readonlyrole/role.yaml");
        roleYaml = roleYaml.Replace("<NAME>", userName);
        var yamlFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(yamlFile, roleYaml);
        await this.ApplyAsync(yamlFile);
    }

    public async Task CreateDiagnosticRoleAsync(string userName)
    {
        // Create a read-only role as well for the user.
        await this.CreateReadOnlyRoleAsync(userName);

        const string telemetryNamespace = "telemetry";

        if (!await this.NamespaceExistsAsync(telemetryNamespace))
        {
            throw new InvalidOperationException(
                $"Namespace '{telemetryNamespace}' does not exist in the cluster. " +
                $"Cannot create diagnostic role.");
        }

        var roleYaml = await File.ReadAllTextAsync("diagnosticrole/role.yaml");
        roleYaml = roleYaml.Replace("<NAMESPACE>", telemetryNamespace);
        roleYaml = roleYaml.Replace("<NAME>", userName);
        var yamlFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(yamlFile, roleYaml);
        await this.ApplyAsync(yamlFile);
    }

    public async Task<string> GetServiceAccountTokenAsync(string saName, string ns)
    {
        var token = await this.Kubectl(
            $"-n {ns} create token {saName} " +
            $"--duration 48h " +
            $"--kubeconfig={this.kubeConfigFile}",
            skipOutputLogging: true);
        return token.Trim().Trim('\'');
    }

    public async Task<K8sPods> GetPods(string ns)
    {
        var output = await this.Kubectl(
            $"get pods " +
            $"-n {ns} " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: false);

        var pods = JsonSerializer.Deserialize<K8sPods>(output)!;
        return pods;
    }

    public async Task<bool> NodeExistsAsync(string nodeName)
    {
        try
        {
            await this.Kubectl(
                $"get node {nodeName} --kubeconfig={this.kubeConfigFile}",
                skipOutputLogging: true);
            return true;
        }
        catch (ExecuteCommandException e) when (e.Message.Contains("Error from server (NotFound)"))
        {
            return false;
        }
    }

    public async Task GetNodesAsync()
    {
        await this.Kubectl($"get nodes --kubeconfig={this.kubeConfigFile}");
    }

    public async Task<bool> HasFlexNodeLabeledNodeAsync()
    {
        var output = await this.Kubectl(
            $"get nodes -l cleanroom.azure.com/flexnode=true " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: true);

        var list = JsonSerializer.Deserialize<ListItems>(output)!;
        return list.Items.Count > 0;
    }

    public async Task<List<JsonObject>> GetFlexNodesAsync()
    {
        var output = await this.Kubectl(
            $"get nodes -l cleanroom.azure.com/flexnode=true " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: true);

        var list = JsonSerializer.Deserialize<ListItems>(output)!;
        return list.Items;
    }

    public async Task<bool> IsFlexNodeReadyAsync(string nodeName)
    {
        var output = await this.Kubectl(
            $"get nodes -l cleanroom.azure.com/ready=true " +
            $"--field-selector metadata.name={nodeName} " +
            $"-o json " +
            $"--kubeconfig {this.kubeConfigFile}",
            skipOutputLogging: true);

        var list = JsonSerializer.Deserialize<ListItems>(output)!;
        if (list.Items.Count == 0)
        {
            return false;
        }

        // Verify that the KubeletReady condition on the node is True. If node goes unhealthy/stopped
        // this signal will become False.
        var node = list.Items[0];
        var conditions = node["status"]?["conditions"]?.AsArray();
        if (conditions != null)
        {
            foreach (var condition in conditions)
            {
                if (condition?["type"]?.GetValue<string>() == "Ready" &&
                    condition?["status"]?.GetValue<string>() == "True" &&
                    condition?["reason"]?.GetValue<string>() == "KubeletReady")
                {
                    return true;
                }
            }
        }

        return false;
    }

    public async Task TaintNodeAsync(string nodeName, string taint, bool overwrite = false)
    {
        var overwriteFlag = overwrite ? " --overwrite" : string.Empty;
        await this.Kubectl(
            $"taint nodes {nodeName} {taint}{overwriteFlag} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task RemoveTaintNodeAsync(string nodeName, string taintKey)
    {
        try
        {
            await this.Kubectl(
                $"taint nodes {nodeName} {taintKey}- --kubeconfig={this.kubeConfigFile}");
        }
        catch (ExecuteCommandException e)
        when (e.Message.Contains("not found"))
        {
            // Taint does not exist, ignore.
        }
    }

    public async Task LabelNodeAsync(string nodeName, string label, bool overwrite = false)
    {
        var overwriteFlag = overwrite ? " --overwrite" : string.Empty;
        await this.Kubectl(
            $"label nodes {nodeName} {label}{overwriteFlag} --kubeconfig={this.kubeConfigFile}");
    }

    public async Task<ClientCertificateResult> RequestClientCertificateAsync(
        string csrName,
        string commonName)
    {
        // Generate RSA key and CSR
        using var rsa = RSA.Create(2048);
        var certRequest = new CertificateRequest(
            new X500DistinguishedName($"CN={commonName}"),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        byte[] csrDer = certRequest.CreateSigningRequest();
        string csrPem = ToPem("CERTIFICATE REQUEST", csrDer);

        // Write CSR to a temporary file for kubectl apply
        string csrFile = Path.GetTempFileName();
        string csrObject = $@"apiVersion: certificates.k8s.io/v1
kind: CertificateSigningRequest
metadata:
  name: {csrName}
spec:
  request: {Convert.ToBase64String(Encoding.UTF8.GetBytes(csrPem))}
  signerName: kubernetes.io/kube-apiserver-client
  usages:
    - client auth
  groups:
    - system:authenticated
";
        await File.WriteAllTextAsync(csrFile, csrObject);

        // Create CSR in cluster
        await this.Kubectl($"apply -f {csrFile} --kubeconfig={this.kubeConfigFile}");

        // Approve CSR
        await this.Kubectl(
            $"certificate approve {csrName} --kubeconfig={this.kubeConfigFile}");

        // Wait for certificate to be issued
        TimeSpan timeout = TimeSpan.FromMinutes(5);
        var stopwatch = Stopwatch.StartNew();
        string? certBase64 = null;
        while (stopwatch.Elapsed < timeout)
        {
            var output = await this.Kubectl(
                $"get csr {csrName} -o jsonpath='{{.status.certificate}}' " +
                $"--kubeconfig={this.kubeConfigFile}",
                skipOutputLogging: true);
            if (!string.IsNullOrWhiteSpace(output))
            {
                certBase64 = output.ToString().Trim().Trim('\'').Trim('"');
                if (!string.IsNullOrEmpty(certBase64))
                {
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }

        if (string.IsNullOrEmpty(certBase64))
        {
            throw new InvalidOperationException(
                $"Timed out waiting for certificate issuance for CSR {csrName}.");
        }

        string certPem = Encoding.UTF8.GetString(Convert.FromBase64String(certBase64));

        // Export private key in PKCS#8 PEM
        byte[] keyPkcs8 = rsa.ExportPkcs8PrivateKey();
        string keyPem = ToPem("PRIVATE KEY", keyPkcs8);

        try
        {
            await this.Kubectl(
                $"delete csr {csrName} --kubeconfig={this.kubeConfigFile}",
                skipOutputLogging: true);
        }
        catch
        {
            // Best effort cleanup
        }

        return new ClientCertificateResult
        {
            CertificatePem = certPem,
            PrivateKeyPem = keyPem
        };
    }

    public async Task<string> ReplaceUserClientCertificateInKubeConfig(
        string kubeConfig,
        string userName,
        string certificatePem,
        string privateKeyPem)
    {
        var deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();

        var serializer = new SerializerBuilder()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var kubeConfigDoc = deserializer.Deserialize<KubeConfigDocument>(kubeConfig);
        var firstContext = kubeConfigDoc.Contexts.FirstOrDefault() ??
            throw new InvalidOperationException("No contexts found in kubeconfig.");

        var clusterName = !string.IsNullOrEmpty(firstContext.Context?.Cluster) ?
            firstContext.Context!.Cluster :
            firstContext.Name ?? throw new InvalidOperationException(
                "No cluster name found in kubeconfig.");

        kubeConfigDoc.Contexts = new List<KubeConfigDocument.NamedContext>
        {
            new()
            {
                Name = clusterName,
                Context = new KubeConfigDocument.Context
                {
                    Cluster = clusterName,
                    User = userName
                }
            }
        };
        kubeConfigDoc.CurrentContext = clusterName;

        var certData = Convert.ToBase64String(Encoding.UTF8.GetBytes(certificatePem));
        var keyData = Convert.ToBase64String(Encoding.UTF8.GetBytes(privateKeyPem));

        kubeConfigDoc.Users = new List<KubeConfigDocument.NamedUser>
        {
            new()
            {
                Name = userName,
                User = new KubeConfigDocument.UserAuth
                {
                    ClientCertificateData = certData,
                    ClientKeyData = keyData
                }
            }
        };

        return serializer.Serialize(kubeConfigDoc);
    }

    private static string ToPem(string label, byte[] data)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"-----BEGIN {label}-----");
        builder.AppendLine(
            Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks));
        builder.AppendLine($"-----END {label}-----");
        return builder.ToString();
    }

    private async Task KubectlRolloutStatus(string command, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await this.Kubectl("rollout status " + command);
                break;
            }
            catch (ExecuteCommandException e)
            when (e.Message.Contains("timed out waiting for the condition") &&
                stopwatch.Elapsed < timeout)
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
        }
    }

    private async Task KubectlWait(string command, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await this.Kubectl("wait " + command);
                break;
            }
            catch (ExecuteCommandException e)
            when (e.Message.Contains("timed out waiting for the condition") &&
                stopwatch.Elapsed < timeout)
            {
                // Workaround for https://github.com/kubernetes/kubectl/issues/1120 where in wait
                // can hang indefinitely if the object it started waiting on gets deleted.
                // This tends to happen when a pod/deployment is getting upgraded and the older
                // pod/deployments get removed but the wait command latched onto the older
                // instances.
                await Task.Delay(TimeSpan.FromSeconds(10));
            }
            catch (ExecuteCommandException e)
            when (e.Message.Contains("no matching resources found") &&
                stopwatch.Elapsed < timeout)
            {
                // At times wait starts sooner than the resource got applied and appears in k8s.
                // So try again on the assumption that the resource will show up.
                var delay = TimeSpan.FromSeconds(5);
                this.logger.LogInformation($"Can't locate resource for {command}. " +
                    $"Waiting for {delay.TotalSeconds}s and trying again...");
                await Task.Delay(delay);
            }
            catch (ExecuteCommandException e)
            when (e.Message.Contains("not found") &&
                stopwatch.Elapsed < timeout)
            {
                // At times end up waiting on a pod that is about to be deleted.
                // So try again on the assumption that the pod replacing the deleted pod will stay.
                var delay = TimeSpan.FromSeconds(5);
                this.logger.LogInformation($"{e.Message}: " +
                    $"Waiting for {delay.TotalSeconds}s and trying again...");
                await Task.Delay(delay);
            }
        }
    }

    private async Task<K8sEvents> GetPodFailureEvents(string ns, string labelValue)
    {
#pragma warning disable MEN002 // Line is too long
        // Try to gather the pod events as those help in figuring out the issue. Eg for
        // cce policy violation failures one sees messages like:
        // Error: Status(StatusCode="InvalidArgument",
        // Detail="CCE Policy Violation CorrelationId: '700ee57d-07ad-467c-b5d6-e08f9b723eb8',
        // ActivityId: '76e8fc64-cad6-4e6a-9fa7-7da114e65c94',
        // Error:  container creation denied due to policy:
        // policyDecision< eyJkZWNpc2lvbiI6ImRlbnkiLCJyZWFzb24iOnsiZXJyb3JzIjpbImludmFsaWQgY29tbWFuZCJdfSwidHJ1bmNhdGVkIjpbImlncHV0Il19 >policyDecision: unknown")
#pragma warning restore MEN002 // Line is too long
        var allPodsEvents = new K8sEvents();
        allPodsEvents.Items = new List<K8sEvents.K8sEvent>();
        try
        {
            var output = await this.Kubectl($"get pods " +
                $"-n {ns} -l=app.kubernetes.io/name={labelValue} -o name " +
                $"--kubeconfig={this.kubeConfigFile}");
            var pods = output.Split("\n", StringSplitOptions.RemoveEmptyEntries);
            foreach (var pod in pods)
            {
                var podName = pod.Split("/", StringSplitOptions.RemoveEmptyEntries).Last();
                foreach (var reason in new[] { "Failed", "FailedCreatePodSandBox" })
                {
                    try
                    {
                        var events = await this.Kubectl("get events " +
                            $"-n {ns} --field-selector " +
                            $"involvedObject.name={podName},reason={reason} " +
                            $"-o json --kubeconfig={this.kubeConfigFile}");
                        var k8sEvents = JsonSerializer.Deserialize<K8sEvents>(events)!;
                        k8sEvents.Items.ForEach(i => allPodsEvents.Items.Add(i));
                    }
                    catch (Exception ex)
                    {
                        this.logger.LogError(
                            ex,
                            $"Failed to get events for pod {podName} filter: {reason}. Ignoring.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            this.logger.LogError(
                ex,
                $"Failed querying pod events for {labelValue}. Ignoring.");
        }

        return allPodsEvents;
    }

    private async Task<string> Kubectl(
        string args,
        bool skipOutputLogging = false)
    {
        StringBuilder output = new();
        StringBuilder error = new();
        var binary = Environment.ExpandEnvironmentVariables(
            this.config["KUBECTL_PATH"] ?? "kubectl");

        await this.ExecuteCommand(binary, args, output, error, skipOutputLogging);
        return output.ToString();
    }

    public class K8sPods
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = default!;

        [JsonPropertyName("items")]
        public List<K8sPod> Items { get; set; } = default!;

        public class K8sPod
        {
            [JsonPropertyName("metadata")]
            public K8sPodMetadata Metadata { get; set; } = default!;

            [JsonPropertyName("status")]
            public K8sPodStatus Status { get; set; } = default!;
        }

        public class K8sPodMetadata
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = default!;
        }

        public class K8sPodStatus
        {
            [JsonPropertyName("containerStatuses")]
            public List<K8sContainerStatus> ContainerStatuses { get; set; } = default!;

            [JsonPropertyName("initContainerStatuses")]
            public List<K8sContainerStatus> InitContainerStatuses { get; set; } = default!;
        }

        public class K8sContainerStatus
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = default!;

            [JsonPropertyName("ready")]
            public bool Ready { get; set; } = default!;

            [JsonPropertyName("state")]
            public JsonObject State { get; set; } = default!;

            public (string ReasonCode, string ReasonMessage) GetReason()
            {
                var stateObject = this.State["terminated"] ?? this.State["waiting"];

                string? reason = null;
                string? message = null;
                if (stateObject != null)
                {
                    reason = stateObject["reason"]?.ToString();
                    message = stateObject["message"]?.ToString();
                }

                return (
                    reason ?? "NotReady",
                    $"Container {this.Name} is not ready. Message: {message}.");
            }
        }
    }

    public class K8sServices
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = default!;

        [JsonPropertyName("items")]
        public List<K8sService> Items { get; set; } = default!;

        public class K8sService
        {
            [JsonPropertyName("metadata")]
            public Metadata Metadata { get; set; } = default!;

            [JsonPropertyName("spec")]
            public K8sServiceSpec Spec { get; set; } = default!;

            [JsonPropertyName("status")]
            public K8sServiceStatus Status { get; set; } = default!;
        }

        public class Metadata
        {
            [JsonPropertyName("annotations")]
            public Dictionary<string, string> Annotations { get; set; } = default!;

            [JsonPropertyName("name")]
            public string Name { get; set; } = default!;

            [JsonPropertyName("namespace")]
            public string Namespace { get; set; } = default!;
        }

        public class K8sServiceSpec
        {
            [JsonPropertyName("clusterIP")]
            public string ClusterIP { get; set; } = default!;

            [JsonPropertyName("type")]
            public string Type { get; set; } = default!;
        }

        public class K8sServiceStatus
        {
            [JsonPropertyName("loadBalancer")]
            public LoadBalancer LoadBalancer { get; set; } = default!;
        }

        public class LoadBalancer
        {
            [JsonPropertyName("ingress")]
            public List<Ingress> Ingress { get; set; } = default!;
        }

        public class Ingress
        {
            [JsonPropertyName("ip")]
            public string IP { get; set; } = default!;
        }
    }

    public class K8sEvents
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = default!;

        [JsonPropertyName("items")]
        public List<K8sEvent> Items { get; set; } = default!;

        public class K8sEvent
        {
            [JsonPropertyName("metadata")]
            public Metadata Metadata { get; set; } = default!;

            [JsonPropertyName("message")]
            public string Message { get; set; } = default!;

            [JsonPropertyName("reason")]
            public string Reason { get; set; } = default!;

            [JsonPropertyName("count")]
            public int Count { get; set; } = default!;

            [JsonPropertyName("type")]
            public string Type { get; set; } = default!;

            [JsonPropertyName("involvedObject")]
            public InvolvedObject InvolvedObject { get; set; } = default!;

            [JsonPropertyName("lastTimestamp")]
            public string LastTimestamp { get; set; } = default!;

            [JsonPropertyName("firstTimestamp")]
            public string FirstTimestamp { get; set; } = default!;

            [JsonPropertyName("source")]
            public Source Source { get; set; } = default!;
        }

        public class Metadata
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = default!;

            [JsonPropertyName("namespace")]
            public string Namespace { get; set; } = default!;
        }

        public class InvolvedObject
        {
            [JsonPropertyName("kind")]
            public string Kind { get; set; } = default!;

            [JsonPropertyName("name")]
            public string Name { get; set; } = default!;

            [JsonPropertyName("namespace")]
            public string Namespace { get; set; } = default!;
        }

        public class Source
        {
            [JsonPropertyName("component")]
            public string Component { get; set; } = default!;

            [JsonPropertyName("host")]
            public string Host { get; set; } = default!;
        }
    }

    public class ListItems
    {
        [JsonPropertyName("apiVersion")]
        public string ApiVersion { get; set; } = default!;

        [JsonPropertyName("items")]
        public List<JsonObject> Items { get; set; } = default!;
    }

    public class KubeConfigDocument
    {
        [YamlMember(Alias = "current-context")]
        public string? CurrentContext { get; set; }

        [YamlMember(Alias = "contexts")]
        public List<NamedContext> Contexts { get; set; } = new();

        [YamlMember(Alias = "users")]
        public List<NamedUser> Users { get; set; } = new();

        [YamlMember(Alias = "clusters")]
        public List<NamedCluster> Clusters { get; set; } = new();

        public class NamedContext
        {
            [YamlMember(Alias = "name")]
            public string? Name { get; set; }

            [YamlMember(Alias = "context")]
            public Context? Context { get; set; }
        }

        public class Context
        {
            [YamlMember(Alias = "cluster")]
            public string? Cluster { get; set; }

            [YamlMember(Alias = "user")]
            public string? User { get; set; }
        }

        public class NamedUser
        {
            [YamlMember(Alias = "name")]
            public string? Name { get; set; }

            [YamlMember(Alias = "user")]
            public UserAuth? User { get; set; }
        }

        public class UserAuth
        {
            [YamlMember(Alias = "token")]
            public string? Token { get; set; }

            [YamlMember(Alias = "client-certificate-data")]
            public string? ClientCertificateData { get; set; }

            [YamlMember(Alias = "client-key-data")]
            public string? ClientKeyData { get; set; }
        }

        public class NamedCluster
        {
            [YamlMember(Alias = "name")]
            public string? Name { get; set; }

            [YamlMember(Alias = "cluster")]
            public Cluster? Cluster { get; set; }
        }

        public class Cluster
        {
            [YamlMember(Alias = "server")]
            public string? Server { get; set; }

            [YamlMember(Alias = "certificate-authority-data")]
            public string? CertificateAuthorityData { get; set; }
        }
    }

    public class ClientCertificateResult
    {
        public required string CertificatePem { get; set; }

        public required string PrivateKeyPem { get; set; }
    }
}