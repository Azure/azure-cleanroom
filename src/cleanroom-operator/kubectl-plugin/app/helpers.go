package app

import (
	"fmt"
	"net"
	"os/exec"
	"time"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/apimachinery/pkg/runtime/schema"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/tools/clientcmd"
)

var ccfMemberGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "ccfmembers",
}

var ccfUserGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "ccfusers",
}

var governanceServiceGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "governanceservices",
}

var governanceContractGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "governancecontracts",
}

var workloadGovernanceGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "workloadgovernances",
}

func getClientset() (*kubernetes.Clientset, error) {
	loadingRules :=
		clientcmd.NewDefaultClientConfigLoadingRules()
	if kubeconfigPath != "" {
		loadingRules.ExplicitPath = kubeconfigPath
	}
	configOverrides := &clientcmd.ConfigOverrides{}
	kubeConfig :=
		clientcmd.NewNonInteractiveDeferredLoadingClientConfig(
			loadingRules, configOverrides,
		)

	config, err := kubeConfig.ClientConfig()
	if err != nil {
		return nil, fmt.Errorf(
			"loading kubeconfig: %w", err,
		)
	}

	return kubernetes.NewForConfig(config)
}

// startPortForward shells out to kubectl port-forward and
// returns the process, the local port, and any error.
func startPortForward(
	namespace string,
	podName string,
	localPort int,
) (*exec.Cmd, int, error) {
	return startPortForwardToPort(
		namespace, podName, localPort, 18888,
	)
}

// startPortForwardToPort shells out to kubectl port-forward
// to the specified remote port.
func startPortForwardToPort(
	namespace string,
	podName string,
	localPort int,
	remotePort int,
) (*exec.Cmd, int, error) {
	// Find a free port if requested port is in use.
	port := localPort
	for i := 0; i < 10; i++ {
		ln, err := net.Listen(
			"tcp", fmt.Sprintf(":%d", port),
		)
		if err == nil {
			ln.Close()
			break
		}
		port++
	}

	args := []string{
		"port-forward",
		"-n", namespace,
		podName,
		fmt.Sprintf("%d:%d", port, remotePort),
	}
	if kubeconfigPath != "" {
		args = append(
			[]string{"--kubeconfig", kubeconfigPath},
			args...,
		)
	}
	cmd := exec.Command("kubectl", args...)
	if err := cmd.Start(); err != nil {
		return nil, 0, fmt.Errorf(
			"starting port-forward: %w", err,
		)
	}

	// Wait briefly for port-forward to establish.
	time.Sleep(2 * time.Second)

	return cmd, port, nil
}

func logOptions(
	containerName string,
) corev1.PodLogOptions {
	return corev1.PodLogOptions{
		Container: containerName,
	}
}
