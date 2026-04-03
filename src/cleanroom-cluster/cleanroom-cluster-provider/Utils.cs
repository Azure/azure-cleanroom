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
                Constants.SparkOperatorNamespace
            ];
        foreach (string ns in namespacesOfInterest)
        {
            K8sPods pods = await kubectlClient.GetPods(ns);
            foreach (K8sPod pod in pods.Items)
            {
                var podHealth = new PodHealth(ns, pod.Metadata.Name);
                clusterHealth.PodHealth.Add(podHealth);

                if (pod?.Status?.ContainerStatuses != null)
                {
                    var unreadyContainers = pod.Status.ContainerStatuses.Where(x => !x.Ready);
                    if (unreadyContainers.Any())
                    {
                        podHealth.Status = PodStatus.Error;
                        podHealth.Reasons.AddRange(unreadyContainers.Select(ToHealthReason));
                    }
                }

                if (pod?.Status?.InitContainerStatuses != null)
                {
                    var unreadyContainers = pod.Status.InitContainerStatuses.Where(x => !x.Ready);
                    if (unreadyContainers.Any())
                    {
                        podHealth.Status = PodStatus.Error;
                        podHealth.Reasons.AddRange(unreadyContainers.Select(ToHealthReason));
                    }
                }
            }
        }

        return clusterHealth;
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
