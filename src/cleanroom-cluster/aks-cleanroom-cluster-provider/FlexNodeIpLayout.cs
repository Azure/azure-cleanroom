// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace AksCleanRoomProvider;

/// <summary>
/// Holds all IP layout information for a flex node.
/// </summary>
internal record FlexNodeIpInfo(
    string NodeIp,
    string Gateway,
    string Subnet,
    string PodRangeStart,
    string PodRangeEnd,
    int TotalPodIps,
    int SubnetIndex,
    int NodeOrdinal)
{
    public string PodIp(int podIndex) =>
        FlexNodeIpLayout.PodIp(this.SubnetIndex, this.NodeOrdinal, podIndex);
}

/// <summary>
/// Computes IP addresses for a flex node's /24 subnet. Each node gets a
/// dedicated /24 at 10.{secondOctet}.{nodeOrdinal}.0/24 where the second
/// octet is derived from the subnet index (starting at 10).
/// </summary>
internal static class FlexNodeIpLayout
{
    // Where flex node /16 subnets start in the second octet i.e. (10.10.0.0/16)
    internal const int SubnetStartOctet = 10;

    // Last usable second octet (10.17.0.0/16) i.e. total 8 subnets (10.10 - 10.17) for flex nodes
    internal const int SubnetEndOctet = 17;

    // Total available /16 subnets for flex nodes (10.10 - 10.17).
    internal const int MaxSubnets = SubnetEndOctet - SubnetStartOctet + 1;

    // A /16 has 256 /24 blocks, so 256 nodes per subnet.
    internal const int NodesPerSubnet = 256;

    //Supported flexnode count 8 subnets × 256 nodes/subnet = 2048.
    internal const int MaxFlexNodePerCluster = 2048;

    // Each node gets a /24 with 256 IPs. Out of these, 4 are reserved for Azure and
    // we want to reserve an additional 30% buffer for pod density growth, which leaves
    // us with around max 110 pods per node.
    internal const int ReservedIpsPerNode = 256;
    internal const int AzureReservedIps = 4;
    internal const int MaxPodsAllowedPerNode = 110;
    internal const int IpsPerSubnet = 65536;

    public static int GetRequiredSubnetCount(int nodeCount) =>
        (int)Math.Ceiling(
            (double)(nodeCount * ReservedIpsPerNode) / IpsPerSubnet);

    public static FlexNodeIpInfo GetNodeIpInfo(int subnetIndex, int nodeOrdinal)
    {
        int totalPodIpsWithBuffer = MaxPodsAllowedPerNode +
            (int)Math.Ceiling(0.3 * MaxPodsAllowedPerNode);

        return new FlexNodeIpInfo(
            NodeIp: NodeIp(subnetIndex, nodeOrdinal),
            Gateway: Gateway(subnetIndex, nodeOrdinal),
            Subnet: Subnet(subnetIndex, nodeOrdinal),
            PodRangeStart: PodRangeStart(subnetIndex, nodeOrdinal),
            PodRangeEnd: PodRangeEnd(subnetIndex, nodeOrdinal, totalPodIpsWithBuffer),
            TotalPodIps: totalPodIpsWithBuffer,
            SubnetIndex: subnetIndex,
            NodeOrdinal: nodeOrdinal);
    }

    public static string PodIp(int subnetIndex, int nodeOrdinal, int podIndex)
    {
        return Ip(subnetIndex, nodeOrdinal, AzureReservedIps + podIndex + 1);
    }

    private static string NodeIp(int subnetIndex, int nodeOrdinal)
    {
        return Ip(subnetIndex, nodeOrdinal, AzureReservedIps);
    }

    private static string Gateway(int subnetIndex, int nodeOrdinal)
    {
        return Ip(subnetIndex, nodeOrdinal, 1);
    }

    private static string Subnet(int subnetIndex, int nodeOrdinal)
    {
        return $"{Prefix(subnetIndex, nodeOrdinal)}.0/24";
    }

    private static string PodRangeStart(int subnetIndex, int nodeOrdinal)
    {
        return Ip(subnetIndex, nodeOrdinal, AzureReservedIps + 1);
    }

    private static string PodRangeEnd(
        int subnetIndex,
        int nodeOrdinal,
        int totalPodIps)
    {
        return Ip(subnetIndex, nodeOrdinal, AzureReservedIps + totalPodIps);
    }

    private static int GetSubnetSecondOctet(int subnetIndex)
    {
        return SubnetStartOctet + subnetIndex;
    }

    private static string Prefix(int subnetIndex, int nodeOrdinal)
    {
        return $"10.{GetSubnetSecondOctet(subnetIndex)}.{nodeOrdinal}";
    }

    private static string Ip(int subnetIndex, int nodeOrdinal, int hostOctet)
    {
        return $"{Prefix(subnetIndex, nodeOrdinal)}.{hostOctet}";
    }
}