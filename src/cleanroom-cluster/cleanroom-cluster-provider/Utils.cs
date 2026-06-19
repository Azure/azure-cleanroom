// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.RepresentationModel;
using static CleanRoomProvider.KubectlClient;
using static CleanRoomProvider.KubectlClient.K8sPods;

namespace CleanRoomProvider;

public static class Utils
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    public static string GetUniqueString(string id, int length = 13)
    {
        using (var hash = SHA512.Create())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(id);
            var hashedInputBytes = hash.ComputeHash(bytes);
            List<char> a = new();
            for (int i = 1; i <= length; i++)
            {
                var b = hashedInputBytes[i];
                var x = (char)((b % 26) + (byte)'a');
                a.Add(x);
            }

            return new string(a.ToArray());
        }
    }

    public static async Task<string> GetUserKubeConfigAsync(
        KubectlClient kubectlClient,
        string baseKubeConfig,
        string userName)
    {
        string csrName = $"{userName}-{Guid.NewGuid().ToString("N")[..8]}";
        var clientCert = await kubectlClient.RequestClientCertificateAsync(csrName, userName);
        var kubeconfig = await kubectlClient.ReplaceUserClientCertificateInKubeConfig(
            baseKubeConfig,
            userName,
            clientCert.CertificatePem,
            clientCert.PrivateKeyPem);
        return kubeconfig;
    }

    public static async Task<CleanRoomClusterHealth> GetClusterHealth(KubectlClient kubectlClient)
    {
        CleanRoomClusterHealth clusterHealth = new();

        List<string> namespacesOfInterest =
            [
                Constants.AnalyticsAgentNamespace,
                Constants.SparkFrontendServiceNamespace,
                Constants.SparkOperatorNamespace,
                Constants.AnalyticsWorkloadNamespace
            ];
        foreach (string ns in namespacesOfInterest)
        {
            K8sPods pods = await kubectlClient.GetPods(ns);
            foreach (K8sPod pod in pods.Items)
            {
                // Skip completed pods (Succeeded phase) as they are expected to
                // have non-ready containers after finishing their work.
                if (string.Equals(
                    pod?.Status?.Phase, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // In the analytics workload namespace, skip Failed phase pods.
                // Spark driver/executor pods end in Failed when a query errors
                // out (OOM, bad SQL, data issue). This is normal application
                // behavior and not an infrastructure health problem.
                if (ns == Constants.AnalyticsWorkloadNamespace &&
                    string.Equals(
                        pod?.Status?.Phase,
                        "Failed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var podHealth = new PodHealth(ns, pod!.Metadata.Name);
                clusterHealth.PodHealth.Add(podHealth);

                if (pod?.Status?.ContainerStatuses != null)
                {
                    var errorContainers =
                        pod.Status.ContainerStatuses.Where(x => x.IsInErrorState());
                    if (errorContainers.Any())
                    {
                        podHealth.Status = PodStatus.Error;
                        podHealth.Reasons.AddRange(
                            errorContainers.Select(ToHealthReason));
                    }
                }

                if (pod?.Status?.InitContainerStatuses != null)
                {
                    var errorContainers =
                        pod.Status.InitContainerStatuses.Where(x => x.IsInErrorState());
                    if (errorContainers.Any())
                    {
                        podHealth.Status = PodStatus.Error;
                        podHealth.Reasons.AddRange(
                            errorContainers.Select(ToHealthReason));
                    }
                }

                // For pods not yet flagged as error, check if any container
                // has been stuck in ContainerCreating for over 10 minutes.
                // If so, query pod events to see if there is an underlying
                // failure (e.g. FailedCreatePodSandBox due to capacity).
                if (podHealth.Status != PodStatus.Error)
                {
                    bool hasStuckCreating = HasStuckContainerCreating(
                        pod!, TimeSpan.FromMinutes(10));
                    if (hasStuckCreating)
                    {
                        var events =
                            await kubectlClient.GetPodFailureEventsByName(
                                ns, pod!.Metadata.Name);
                        if (events.Items.Count > 0)
                        {
                            podHealth.Status = PodStatus.Error;
                            podHealth.Reasons.AddRange(
                                events.Items.Select(e => new Reason()
                                {
                                    Code = e.Reason,
                                    Message = e.Message,
                                }));
                        }
                    }
                }
            }
        }

        return clusterHealth;
    }

    private static bool HasStuckContainerCreating(
        K8sPod pod,
        TimeSpan threshold)
    {
        // Check if the pod has been alive longer than the threshold.
        if (pod.Metadata.CreationTimestamp == null ||
            !DateTime.TryParse(
                pod.Metadata.CreationTimestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out DateTime createdAt))
        {
            return false;
        }

        if (DateTime.UtcNow - createdAt < threshold)
        {
            return false;
        }

        // Check if any container is still waiting in ContainerCreating.
        return HasWaitingReason(pod.Status?.ContainerStatuses, "ContainerCreating") ||
            HasWaitingReason(pod.Status?.InitContainerStatuses, "ContainerCreating");
    }

    private static bool HasWaitingReason(
        List<K8sContainerStatus>? statuses,
        string reason)
    {
        if (statuses == null)
        {
            return false;
        }

        return statuses.Any(c =>
        {
            var waiting = c.State?["waiting"];
            return waiting != null &&
                string.Equals(
                    waiting["reason"]?.ToString(),
                    reason,
                    StringComparison.OrdinalIgnoreCase);
        });
    }

    private static Reason ToHealthReason(K8sContainerStatus containerStatus)
    {
        (var reasonCode, var reasonMessage) = containerStatus.GetReason();
        return new Reason()
        {
            Code = reasonCode,
            Message = reasonMessage,
        };
    }
}
