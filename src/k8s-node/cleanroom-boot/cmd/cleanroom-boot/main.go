// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// cleanroom-boot is the Cleanroom Flex Node boot configuration
// pipeline. It reads a JSON configuration envelope dropped by cloud-init,
// validates it, extracts individual config files, and applies them in
// order: networking (netplan, CNI), THIM cert warming, flex-node agent,
// GPU setup, proxy installs, and finally node labeling.
package main

import (
	"encoding/json"
	"flag"
	"os"

	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/stages"
	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/status"
	log "github.com/sirupsen/logrus"
)

// pipelineStages is the ordered list of stages executed at boot.
var pipelineStages = []stages.Stage{
	// 1. Read, validate, and extract the config envelope into
	//    individual files. Must be first — all later stages
	//    depend on the parsed config and extracted files.
	&stages.LoadConfigStage{},

	// 2. Apply static netplan for pod networking. Must run
	//    before THIM (needs network) and flex-node agent.
	&stages.ApplyNetplanStage{},

	// 3. Write the CNI bridge config for pod-to-pod networking.
	&stages.ApplyCniStage{},

	// 4. Warm the THIM certificate cache by polling the IMDS
	//    endpoint. Runs after networking is up; also serves as
	//    a network readiness gate since the IMDS poll retries
	//    until the network stack is routable. Must complete
	//    before any attestation flow (governance, CCF).
	&stages.WaitForTHIMProvisioningStage{},

	// 5. This stage resolves the cluster config to get the API
	//    server details and creates the upstream kubeconfig for
	//    the api-server-proxy. It also generates self-signed TLS
	//    certs for the proxy and starts the service. The proxy
	//    URL and the self signed CA cert are injected into the
	//    flex node configuration so that when kubelet starts up,
	//    it connects to the proxy thinking it is the API server
	//    and the proxy can then forward the requests to the
	//    actual API server.
	&stages.StartApiServerProxyStage{},

	// 6. This stage starts the kubelet-proxy service on port
	//    10250. It generates a client cert that is signed by the
	//    api server proxy's self-signed CA cert. The
	//    api-server-proxy intercepts the node registration / node
	//    status requests from kubelet and replaces the kubelet
	//    port with 10250. This is so that the requests the API
	//    server makes to kubelet reach the proxy instead,which
	//    can apply a policy to determine whether to allow or
	//    reject the request. The kubelet-proxy's client
	//    certificate needs to be signed by the api-server-proxy's
	//    self-signed CA cert so that the kubelet will trust it as
	//    it thinks the proxy is the API server. Otherwise, the
	//    calls that are made by the kubelet-proxy to kubelet will
	//    fail with "Unauthorised".
	&stages.StartKubeletProxyStage{},

	// 7. Write the flex-node config with the api-server-proxy
	//    details injected, start the flex-node agent, and wait
	//    for kubelet to become ready. When kubelet starts up, it
	//    has no knowledge of the actual API server and connects
	//    only to the api-server-proxy. Since the proxy starts up
	//    before the kubelet, it can intercept all requests and
	//    apply a policy to filter the responses.
	&stages.StartFlexNodeAgentStage{},

	// 8. Install NVIDIA container runtime, device plugin, and
	//    GPU feature discovery. Must run after flex-node so
	//    that containerd is available for runtime configuration.
	&stages.ConfigureGpuStage{},

	// 9. Label the node with boot-complete=true. Must be last
	//     — the cluster provider waits for this label before
	//     applying taints and scheduling workloads.
	&stages.LabelNodeBootCompleteStage{},
}

func main() {
	setupLogging()

	configPath := flag.String(
		"config", "",
		"Path to the cleanroom-config.json envelope.",
	)
	bootDir := flag.String(
		"boot-dir", "",
		"Directory for extracted boot config files.",
	)
	apiServerProxyDir := flag.String(
		"api-server-proxy-dir", "",
		"Directory containing api-server-proxy binary and install.sh.",
	)
	kubeletProxyDir := flag.String(
		"kubelet-proxy-dir", "",
		"Directory containing kubelet-proxy binary and install.sh.",
	)
	flag.Parse()

	if *configPath == "" || *bootDir == "" ||
		*apiServerProxyDir == "" || *kubeletProxyDir == "" {
		flag.Usage()
		os.Exit(1)
	}

	bootStatus := status.New()

	log.Info("=== Cleanroom Flex Node Boot ===")
	log.Infof("Image version: %s", bootStatus.ImageVersion)
	log.Infof("Config file:   %s", *configPath)

	ctx := &stages.Context{
		ConfigPath:        *configPath,
		BootDir:           *bootDir,
		ApiServerProxyDir: *apiServerProxyDir,
		KubeletProxyDir:   *kubeletProxyDir,
		BootStatus:        bootStatus,
	}

	for _, s := range pipelineStages {
		bootStatus.Stage = s.Name()
		if err := bootStatus.Write(); err != nil {
			log.Warnf("Failed to write status: %v", err)
		}
		log.Infof("--- Stage: %s ---", s.Name())

		if err := s.Run(ctx); err != nil {
			bootStatus.Status = "failed"
			bootStatus.Error = err.Error()
			bootStatus.SetComponent(
				s.Name(), "failed", status.WithError(err.Error()),
			)
			_ = bootStatus.Write()
			log.Errorf("Stage '%s' failed: %v", s.Name(), err)
			dumpStatus(bootStatus)
			os.Exit(1)
		}

		// If the stage didn't already set a component status,
		// mark it as succeeded.
		if _, exists := bootStatus.Components[s.Name()]; !exists {
			bootStatus.SetComponent(s.Name(), "succeeded")
		}
	}

	log.Info("=== Boot configuration completed successfully ===")
	bootStatus.Status = "ready"
	bootStatus.Stage = "complete"
	_ = bootStatus.Write()
	dumpStatus(bootStatus)
}

func setupLogging() {
	log.SetOutput(os.Stdout)
	log.SetLevel(log.InfoLevel)
	log.SetFormatter(&log.TextFormatter{
		FullTimestamp:   false,
		DisableColors:   true,
		TimestampFormat: "",
		// Prefix similar to the Python format.
		ForceColors: false,
	})
}

func dumpStatus(bootStatus *status.BootStatus) {
	data := map[string]any{
		"status":        bootStatus.Status,
		"stage":         bootStatus.Stage,
		"error":         bootStatus.Error,
		"configVersion": bootStatus.ConfigVersion,
		"imageVersion":  bootStatus.ImageVersion,
		"components":    bootStatus.Components,
	}
	jsonBytes, err := json.Marshal(data)
	if err != nil {
		log.Warnf("Failed to marshal status for log: %v", err)
		return
	}
	log.Infof("CLEANROOM_BOOT_STATUS_JSON=%s", string(jsonBytes))
}
