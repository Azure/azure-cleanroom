// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"context"
	"fmt"
	"os"
	"time"

	log "github.com/sirupsen/logrus"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/types"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/tools/clientcmd"
)

const (
	labelKey      = "cleanroom.azure.com/boot-complete"
	labelValue    = "true"
	maxRetries    = 12
	retryInterval = 5 * time.Second
)

// kubeconfigPaths lists the known locations for the kubelet
// kubeconfig, checked in order. AKS Flex places it under an
// extra kubelet/ directory.
var kubeconfigPaths = []string{
	"/var/lib/kubelet/kubelet/kubeconfig", // AKS Flex
	"/var/lib/kubelet/kubeconfig",         // Standard AKS
}

// LabelNodeBootCompleteStage labels this node to signal that
// cleanroom-boot has finished.
type LabelNodeBootCompleteStage struct{}

func (s *LabelNodeBootCompleteStage) Name() string { return "nodeLabel" }

func (s *LabelNodeBootCompleteStage) Run(_ *Context) error {
	hostname, err := os.Hostname()
	if err != nil {
		return fmt.Errorf("getting hostname: %w", err)
	}

	label := fmt.Sprintf("%s=%s", labelKey, labelValue)
	log.Infof("Labeling node %s with %s ...", hostname, label)

	// Find the kubelet kubeconfig at a known path.
	var kubeconfigPath string
	for _, p := range kubeconfigPaths {
		if _, err := os.Stat(p); err == nil {
			kubeconfigPath = p
			break
		}
	}
	if kubeconfigPath == "" {
		return fmt.Errorf(
			"kubelet kubeconfig not found at any of %v",
			kubeconfigPaths,
		)
	}
	log.Infof("Using kubeconfig: %s", kubeconfigPath)

	cfg, err := clientcmd.BuildConfigFromFlags("", kubeconfigPath)
	if err != nil {
		return fmt.Errorf("loading kubeconfig: %w", err)
	}

	clientset, err := kubernetes.NewForConfig(cfg)
	if err != nil {
		return fmt.Errorf("creating kubernetes client: %w", err)
	}

	v1 := clientset.CoreV1().Nodes()
	ctx := context.Background()

	// Wait for the node object to appear (kubelet self-registration).
	log.Infof("Waiting for node %s to be registered ...", hostname)
	registered := false
	for attempt := 1; attempt <= maxRetries; attempt++ {
		_, getErr := v1.Get(ctx, hostname, metav1.GetOptions{})
		if getErr == nil {
			registered = true
			log.Infof("Node %s is registered.", hostname)
			break
		}
		if attempt < maxRetries {
			log.Infof(
				"Node %s not yet registered (attempt %d/%d), "+
					"retrying in %v ...",
				hostname, attempt, maxRetries, retryInterval,
			)
			time.Sleep(retryInterval)
		} else {
			return fmt.Errorf(
				"node %s not registered after %d attempts: %w",
				hostname, maxRetries, getErr,
			)
		}
	}

	if !registered {
		return fmt.Errorf(
			"node %s not registered after %d attempts",
			hostname, maxRetries,
		)
	}

	// Apply the label.
	patch := fmt.Sprintf(
		`{"metadata":{"labels":{"%s":"%s"}}}`, labelKey, labelValue,
	)
	_, err = v1.Patch(
		ctx, hostname, types.MergePatchType,
		[]byte(patch), metav1.PatchOptions{},
	)
	if err != nil {
		return fmt.Errorf("patching node label: %w", err)
	}
	log.Infof("Node labeled: %s", label)
	return nil
}
