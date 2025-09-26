// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CleanRoomProvider;

public class KubectlClient : RunCommand
{
    // It can take a while as containers start on caci.
    private static readonly TimeSpan CaciWaitTimeout = TimeSpan.FromMinutes(8);

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
                endpoint = service.Status.LoadBalancer.Ingress.FirstOrDefault()?.IP;
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

    public async Task InstallGrafanaDashboards(string ns)
    {
        var dashboardDir = "observability/grafana/dashboards";
        var output = await this.Kubectl(
            $"create configmap " +
            $"grafana-dashboards " +
            $"--from-file={dashboardDir} -n {ns} --kubeconfig {this.kubeConfigFile} " +
            $"--dry-run=client -o yaml");
        var configMapFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(configMapFile, output);
        await this.Kubectl($"apply -f {configMapFile} --kubeconfig {this.kubeConfigFile}");
        await this.Kubectl(
            $"label configmap grafana-dashboards " +
            $"grafana_dashboard=1 " +
            $"-n {ns} --kubeconfig {this.kubeConfigFile}");
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
        // policyDecision< eyJkZWNpc2lvbiI6ImRlbnkiLCJyZWFzb24iOnsiZXJyb3JzIjpbImludmFsaWQgY29tbWFuZCJdfSwidHJ1bmNhdGVkIjpbImlucHV0Il19 >policyDecision: unknown")
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
}