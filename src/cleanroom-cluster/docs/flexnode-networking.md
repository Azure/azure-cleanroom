# Flex Node Networking

Flex nodes use a **shared-subnet model** where the VM and its pods share the same
`/24` subnet. All IPs are pre-allocated on the Azure NIC so that Azure VNet routing
handles pod-to-pod traffic natively with no overlay or encapsulation.

## Subnet Layout

Each flex node gets its own `/24` within a `/16` VNet. The second octet is derived
from the subnet index (starting at 10). For example, flexnode with ordinal 5 gets subnet flexnode-0 with Pods in `10.10.5.0/24`.

Each `/24` provides 256 addresses, of which 144 are used:

| Range | Purpose |
|-------|---------|
| `.1` | CNI bridge gateway (`cni0`) |
| `.2`–`.3` | Reserved by Azure to map the Azure DNS IP addresses to the virtual network space. |
| `.4` | FlexNode primary IP |
| `.5`–`.147` | Flexnode Pod IPs (110 + 30% buffer = 143) |

### Examples

| Global Ordinal | Subnet Index | Node Ordinal | Node Subnet | Node IP (NIC primary) | NIC Secondary IPs | CNI rangeStart | CNI rangeEnd | CNI Gateway |
|---|---|---|---|---|---|---|---|---|
| 0 | 0 | 0 | `10.10.0.0/24` | `10.10.0.4` | `10.10.0.5`–`10.10.0.147` | `10.10.0.5` | `10.10.0.147` | `10.10.0.1` |
| 1 | 0 | 1 | `10.10.1.0/24` | `10.10.1.4` | `10.10.1.5`–`10.10.1.147` | `10.10.1.5` | `10.10.1.147` | `10.10.1.1` |
| 2 | 0 | 2 | `10.10.2.0/24` | `10.10.2.4` | `10.10.2.5`–`10.10.2.147` | `10.10.2.5` | `10.10.2.147` | `10.10.2.1` |
| 5 | 0 | 5 | `10.10.5.0/24` | `10.10.5.4` | `10.10.5.5`–`10.10.5.147` | `10.10.5.5` | `10.10.5.147` | `10.10.5.1` |
| 255 | 0 | 255 | `10.10.255.0/24` | `10.10.255.4` | `10.10.255.5`–`10.10.255.147` | `10.10.255.5` | `10.10.255.147` | `10.10.255.1` |
| 700 | 2 | 188 | `10.12.188.0/24` | `10.12.188.4` | `10.12.188.5`–`10.12.188.147` | `10.12.188.5` | `10.12.188.147` | `10.12.188.1` |
| 2047 | 7 | 255 | `10.17.255.0/24` | `10.17.255.4` | `10.17.255.5`–`10.17.255.147` | `10.17.255.5` | `10.17.255.147` | `10.17.255.1` |

The pattern is `10.{SubnetStartOctet + subnetIndex}.{nodeOrdinal}.{offset}` where
`SubnetStartOctet` is 10 (so index 0 → 10, index 1 → 11, etc.). The global
ordinal determines the subnet index (`ordinal / 256`) and node ordinal
(`ordinal % 256`), giving up to 256 nodes per `/16` subnet.

### Dynamic Subnet Creation

Flex node `/16` subnets are **not** pre-created in the VNet. Instead, they are
provisioned dynamically based on the requested flexnode count. During cluster creation
(and updates), `UpdateVnetForFlexNodeAsync` calls
`FlexNodeIpLayout.GetRequiredSubnetCount(nodeCount)` to calculate how many subnets
are needed:

Only the required subnets are added to the VNet. For example:

| Node Count | Required Subnets | Subnet CIDRs |
|---|---|---|
| 1–256 | 1 | `10.10.0.0/16` |
| 257–512 | 2 | `10.10.0.0/16`, `10.11.0.0/16` |
| 513–768 | 3 | `10.10.0.0/16`, `10.11.0.0/16`, `10.12.0.0/16` |
| 2048 | 8 | `10.10.0.0/16` through `10.17.0.0/16` |

The maximum is **8 subnets** (`MaxSubnets`), which caps the cluster at 2048 flex
nodes. Cluster creation validation rejects requests that would exceed this limit.

## NIC IP Pre-allocation

Azure requires every IP used in a VNet to be registered on a NIC.
`FlexNodeProvider.CreateNetworkInterfaceAsync` creates the NIC with:

- **1 primary IP config** (`node-ipconfig`) at `.4` — the flexnode's own address.
- **143 secondary IP configs** (`pod-ipconfig-1` through `pod-ipconfig-143`) at
  `.5`–`.147` — pre-allocated for pod networking (110 pods + 30% buffer for pod
  restarts where old and new pods briefly coexist).

All IPs use static allocation within the node's `/24` subnet.

## Cloud-Init Networking Setup

Both the CNI bridge config and the netplan override are delivered via cloud-init
custom data at VM creation time. Cloud-init executes on first boot — before the
flexnode agent is installed — so by the time the agent starts and kubelet begins
scheduling pods, the bridge config (`10-bridge.conf`) is already in place and the
original cloud-init netplan (`50-cloud-init.yaml`) has been backed up and replaced
by the static netplan (`99-static-eth0.yaml`).

### CNI Bridge Configuration

`FlexNodeProvider.GetCloudInitYaml` generates the cloud-init config that writes a
`host-local` IPAM bridge config to `/etc/cni/net.d/10-bridge.conf`:

```json
{
  "cniVersion": "0.3.1",
  "name": "bridge",
  "type": "bridge",
  "bridge": "cni0",
  "isGateway": true,
  "ipMasq": true,
  "ipam": {
    "type": "host-local",
    "ranges": [[{
      "subnet": "10.10.5.0/24",
      "rangeStart": "10.10.5.5",
      "rangeEnd": "10.10.5.147",
      "gateway": "10.10.5.1"
    }]],
    "routes": [{ "dst": "0.0.0.0/0" }]
  }
}
```

The `rangeStart`/`rangeEnd` match exactly the secondary IPs on the NIC. When
kubelet creates a pod the CNI plugin picks the next available IP from this range.
Since these IPs already exist on the NIC, Azure routes traffic to them correctly.

### Netplan Override

The same cloud-init config writes a static netplan file (`99-static-eth0.yaml`)
that declares **only the primary node IP**, replacing the default cloud-init
netplan config (`50-cloud-init.yaml`):

```yaml
network:
  ethernets:
    eth0:
      addresses:
        - 10.10.5.4/24
      dhcp4: true
      dhcp4-overrides:
        route-metric: 100
      dhcp6: false
  version: 2
```

This is necessary because:

- Cloud-init's default config would try to manage all 144 IPs and conflict with CNI.
- Linux only needs the primary IP for node-level connectivity.
- DHCP stays enabled (metric 100) as a fallback for Azure metadata and DNS.

## DNS Resolution

Our AKS cluster's `kube-dns` service IP is `10.4.0.10` (within the service CIDR),
but flex nodes cannot use it because kube-proxy is not running on them — there are
no iptables rules to route service-CIDR traffic to the CoreDNS pods. Instead, we override
flex nodes default DNS IP (`10.0.0.10`) to use the Azure DNS IP (`168.63.129.16`) for domain
resolution. DHCP in the netplan config provides this automatically.

## Traffic Flow

### Pod-to-pod (same node)

```
Pod A → cni0 bridge → Pod B
```

The bridge forwards directly between pods on the same node.

### Pod-to-pod (cross-node, including managed ↔ flex)

```
Pod → cni0 bridge (gateway .1) → Linux routing → eth0 → Azure VNet → destination NIC → CNI → Pod
```

Because pod IPs are real NIC IPs, Azure VNet routing delivers traffic directly
with no tunnel or encapsulation. All subnets (managed nodes at `10.1.0.0/16`,
flex nodes at `10.10.0.0/16`+) live in the **same VNet**, so Azure fabric-level
layer 3 routing handles cross-subnet traffic natively. No route tables, UDRs, or
VNet peering are required.

This means managed AKS node pods (`10.1.x.x`) can reach flex node pods
(`10.10.x.x`) and vice versa — the Azure network plugin (`NetworkPlugin = Azure`)
treats all pre-allocated NIC IPs as first-class VNet citizens.

### IMDS (169.254.169.254)

Pods can access the Azure Instance Metadata Service (IMDS) at `169.254.169.254`
without any special configuration. Here's why it works:

1. The pod sends a request to `169.254.169.254`.
2. The pod's default route sends it to the `cni0` bridge gateway (`.1`).
3. The bridge lives on the host, so the packet enters the host's network stack.
4. `ipMasq: true` in the CNI config rewrites the source IP from the pod IP to
   the node's primary IP.
5. Azure intercepts `169.254.169.254` at the hypervisor level before it ever
   leaves the host — it never hits the physical network.
6. IMDS responds back to the node IP, and the SNAT is reversed back to the pod.

No iptables rules or proxy setup needed. DHCP remains enabled in netplan
(metric 100) to keep Azure DNS (`168.63.129.16`) reachable.
