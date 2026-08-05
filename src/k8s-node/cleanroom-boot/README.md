# cleanroom-boot

Cleanroom Flex Node boot configuration pipeline. A single static Go
binary that reads a JSON configuration envelope, validates it, extracts
individual config files, and executes an ordered sequence of stages to
bring the node into a ready state.

## Build

```bash
# Build and push the cleanroom-boot binary as an OCI artifact
pwsh build/k8s-node/build-cleanroom-boot.ps1 \
  -repo myacr.azurecr.io -tag latest -push
```

The build uses `build/docker/Dockerfile.cleanroom-boot` to produce a
statically-linked binary (`CGO_ENABLED=0`).

## Usage

```bash
cleanroom-boot \
  --config /etc/cleanroom-boot/cleanroom-config.json \
  --boot-dir /etc/cleanroom-boot \
  --api-server-proxy-dir /opt/api-server-proxy \
  --kubelet-proxy-dir /opt/kubelet-proxy
```

On a baked image, the `initialize-cleanroom.service` systemd unit
invokes the binary automatically at boot.

## Boot Stages

| # | Stage | Name | Description |
|---|-------|------|-------------|
| 1 | `LoadConfigStage` | `loadConfig` | Reads and validates the JSON config envelope, extracts individual config files to the boot directory |
| 2 | `ApplyNetplanStage` | `netplan` | Applies static IP netplan config for pod networking. Must run before THIM and flex-node agent |
| 3 | `ApplyCniStage` | `cni` | Copies CNI bridge config to `/etc/cni/net.d/` |
| 4 | `WaitForTHIMProvisioningStage` | `thimProvisioning` | Polls the IMDS THIM endpoint until certificates are provisioned. Also serves as a network readiness gate |
| 5 | `StartFlexNodeAgentStage` | `flexNodeAgent` | Starts the AKS flex-node agent and waits for kubelet to become ready |
| 6 | `ConfigureGpuStage` | `gpu` | Installs GPU container runtime and configures CC mode. Runs after flex-node so containerd is available |
| 7 | `InstallApiServerProxyStage` | `apiServerProxy` | Runs the api-server-proxy `install.sh` |
| 8 | `InstallKubeletProxyStage` | `kubeletProxy` | Runs the kubelet-proxy `install.sh` |
| 9 | `LabelNodeBootCompleteStage` | `nodeLabel` | Labels node with `cleanroom.azure.com/boot-complete=true` via the Kubernetes API |

## Config Envelope

All boot configuration is passed as a single JSON file
(`/etc/cleanroom-boot/cleanroom-config.json`) via cloud-init
`write_files`:

```json
{
  "version": "1.0",
  "flexNodeConfig": { ... },
  "cniConfig": { ... },
  "netplan": { "network": { ... } },
  "apiServerProxyConfig": {
    "proxyListenAddr": "127.0.0.1:6444",
    "insecure": false,
    "signingCert": "-----BEGIN CERTIFICATE-----\n..."
  },
  "kubeletProxyConfig": {
    "apiPolicy": { ... }
  },
  "gpuConfig": { ... }
}
```

## Status Reporting

The pipeline writes `/var/run/cleanroom/status.json` at each stage
transition:

```json
{
  "status": "ready",
  "stage": "complete",
  "error": "",
  "configVersion": "1.0",
  "imageVersion": "1.0.46",
  "components": {
    "loadConfig": { "status": "succeeded" },
    "netplan": { "status": "succeeded" },
    "cni": { "status": "succeeded" },
    "thimProvisioning": { "status": "succeeded" },
    "flexNodeAgent": { "status": "succeeded" },
    "gpu": { "status": "skipped", "reason": "no GPU detected" },
    "apiServerProxy": { "status": "succeeded" },
    "kubeletProxy": { "status": "succeeded" },
    "nodeLabel": { "status": "succeeded" }
  }
}
```

The status is also dumped to stdout (serial console) as
`CLEANROOM_BOOT_STATUS_JSON={...}` for Azure boot diagnostics.

## Adding a New Stage

1. Create `internal/stages/mystage.go`:

```go
package stages

type MyNewStage struct{}

func (s *MyNewStage) Name() string { return "myNewStage" }

func (s *MyNewStage) Run(ctx *Context) error {
    // Access ctx.Config, ctx.BootDir, ctx.BootStatus
    return nil
}
```

2. Add to `pipelineStages` in `cmd/cleanroom-boot/main.go` at the
   appropriate position.
3. The pipeline runner handles status tracking and error recording
   automatically.

## Monitoring

```bash
# Check boot service status
sudo systemctl status initialize-cleanroom.service

# Follow boot logs in real time
sudo journalctl -u initialize-cleanroom.service -f

# Check boot status
sudo cat /var/run/cleanroom/status.json | jq .
```
