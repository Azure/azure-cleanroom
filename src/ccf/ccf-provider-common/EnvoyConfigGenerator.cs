// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AciLoadBalancer;

public class EnvoyConfigGenerator
{
    public static string GenerateEnvoyYaml(List<TcpService> services)
    {
        /* Sample output:
static_resources:
  listeners:
  - name: listener_9000
    address:
      socket_address:
        address: 0.0.0.0
        port_value: 9000
    filter_chains:
    - filters:
      - name: envoy.filters.network.tcp_proxy
        typed_config:
          "@type": type.googleapis.com/envoy.extensions.filters.network.tcp_proxy.v3.TcpProxy
          stat_prefix: tcp_9000
          cluster: cluster_9000
          access_log:
          - name: envoy.access_loggers.stdout
            typed_config:
              '@type': type.googleapis.com/envoy.extensions.access_loggers.stream.v3.StdoutAccessLog
  - name: listener_9001
    address:
      socket_address:
        address: 0.0.0.0
        port_value: 9001
    filter_chains:
    - filters:
      - name: envoy.filters.network.tcp_proxy
        typed_config:
          "@type": type.googleapis.com/envoy.extensions.filters.network.tcp_proxy.v3.TcpProxy
          stat_prefix: tcp_9001
          cluster: cluster_9001
          access_log:
          - name: envoy.access_loggers.stdout
            typed_config:
              '@type': type.googleapis.com/envoy.extensions.access_loggers.stream.v3.StdoutAccessLog
  clusters:
  - name: cluster_9000
    type: strict_dns
    connect_timeout: 1s
    lb_policy: round_robin
    load_assignment:
      cluster_name: cluster_9000
      endpoints:
      - lb_endpoints:
        - endpoint:
            address:
              socket_address:
                address: backend1.example.com
                port_value: 443

  - name: cluster_9001
    type: strict_dns
    connect_timeout: 1s
    lb_policy: round_robin
    load_assignment:
      cluster_name: cluster_9001
      endpoints:
      - lb_endpoints:
        - endpoint:
            address:
              socket_address:
                address: backend2.example.com
                port_value: 8443
admin:
  access_log_path: /tmp/admin_access.log
  address:
    socket_address:
      address: 0.0.0.0
      port_value: 9901
         */
        var listeners = new List<object>();
        var clusters = new List<object>();

#pragma warning disable MEN002 // Line is too long
        foreach (var svc in services)
        {
            string clusterName = $"cluster_{svc.ListenerPort}";

            // Listener config.
            listeners.Add(new
            {
                name = $"listener_{svc.ListenerPort}",
                address = new
                {
                    socket_address = new
                    {
                        address = "0.0.0.0",
                        port_value = int.Parse(svc.ListenerPort)
                    }
                },
                filter_chains = new[]
                {
                    new
                    {
                        filters = new[]
                        {
                            new
                            {
                                name = "envoy.filters.network.tcp_proxy",
                                typed_config = new Dictionary<string, object>
                                {
                                    { "@type", "type.googleapis.com/envoy.extensions.filters.network.tcp_proxy.v3.TcpProxy" },
                                    { "stat_prefix", $"tcp_{svc.ListenerPort}" },
                                    { "cluster", clusterName },
                                    {
                                        "access_log", new[]
                                        {
                                            new
                                            {
                                                name = "envoy.access_loggers.stdout",
                                                typed_config = new Dictionary<string, object>
                                                {
                                                    { "@type", "type.googleapis.com/envoy.extensions.access_loggers.stream.v3.StdoutAccessLog" },
                                                },
                                                filter = new
                                                {
                                                    response_flag_filter = new
                                                    {
                                                        // See https://www.envoyproxy.io/docs/envoy/latest/configuration/observability/access_log/usage
                                                        flags = new[]
                                                        {
                                                            "UH", // No healthy upstream hosts in upstream cluster
                                                            "UF", // Upstream connection failure
                                                            "UO", // Upstream overflow
                                                            "NR", // No route found upstream
                                                            "URX", // Hit upstream limits
                                                            "NC", // Upstream cluster not found
                                                            "DT", // When a request or connection exceeded
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            });
#pragma warning restore MEN002 // Line is too long

            // Cluster config with multiple endpoints.
            var lbEndpoints = new List<object>();
            foreach (var (address, port) in svc.Upstreams)
            {
                lbEndpoints.Add(new
                {
                    endpoint = new
                    {
                        address = new
                        {
                            socket_address = new
                            {
                                address = address,
                                port_value = int.Parse(port)
                            }
                        }
                    }
                });
            }

            // For docker setups envoy DNS resolution for host.docker.internal resolved to an ipv6
            // address which was not reachable from the container. Hence forcing ipv4.
            bool v4Only = svc.Upstreams.Any(x => x.address == "host.docker.internal");

            clusters.Add(new
            {
                name = clusterName,
                connect_timeout = "60s",
                type = "STRICT_DNS",
                dns_lookup_family = v4Only ? "V4_ONLY" : "AUTO",
                lb_policy = "ROUND_ROBIN",
                load_assignment = new
                {
                    cluster_name = clusterName,
                    endpoints = new[]
                    {
                        new { lb_endpoints = lbEndpoints }
                    }
                }
            });
        }

        var config = new
        {
            static_resources = new
            {
                listeners = listeners,
                clusters = clusters
            },
            admin = new
            {
                access_log_path = "/tmp/admin_access.log",
                address = new
                {
                    socket_address = new
                    {
                        address = "0.0.0.0",
                        port_value = 9901
                    }
                }
            }
        };

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        return serializer.Serialize(config);
    }
}

public class TcpService
{
    public string ListenerPort { get; set; } = default!;

    public IEnumerable<(string address, string port)> Upstreams { get; set; } = [];
}