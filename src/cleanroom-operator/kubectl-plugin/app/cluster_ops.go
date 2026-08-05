package app

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/spf13/cobra"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
	"k8s.io/apimachinery/pkg/runtime/schema"
	"k8s.io/client-go/dynamic"

	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

func newClusterKubeconfigCmd() *cobra.Command {
	var namespace string
	var outputFile string
	var accessRole string

	cmd := &cobra.Command{
		Use:   "kubeconfig <name>",
		Short: "Export kubeconfig for a Clean Room cluster",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterKubeconfig(
				cmd.Context(), args[0],
				namespace, outputFile, accessRole,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	f.StringVarP(&outputFile, "file", "f",
		"", "Output file path for kubeconfig")
	f.StringVar(&accessRole, "access-role",
		"admin", "Access role (admin, readonly, diagnostic)")
	return cmd
}

func runClusterKubeconfig(
	ctx context.Context,
	name, namespace, outputFile, accessRole string,
) error {
	dynClient, err := getDynamicClient()
	if err != nil {
		return err
	}

	// Get the CR to find the provider client endpoint and spec.
	cr, err := dynClient.Resource(clusterGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Cluster: %w", err)
	}

	infraType, _, _ := unstructured.NestedString(
		cr.Object, "spec", "infraType",
	)

	var kubeconfig []byte

	providerConfig, _, _ := unstructured.NestedMap(
		cr.Object, "spec", "providerConfig",
	)

	endpoint, cleanup, err :=
		portForwardToClusterProvider()
	if err != nil {
		return fmt.Errorf(
			"port-forwarding to provider: %w", err,
		)
	}
	defer cleanup()

	cc := client.NewClusterClient(endpoint)

	var pcRaw json.RawMessage
	if providerConfig != nil {
		pcRaw, _ = json.Marshal(providerConfig)
	}

	kubeconfig, err = cc.GetKubeconfig(ctx, name,
		&client.GetKubeconfigInput{
			InfraType:      infraType,
			ProviderConfig: pcRaw,
			AccessRole:     accessRole,
		},
	)
	if err != nil {
		return fmt.Errorf("getting kubeconfig: %w", err)
	}

	if outputFile != "" {
		if err := os.WriteFile(
			outputFile, kubeconfig, 0o600,
		); err != nil {
			return fmt.Errorf("writing kubeconfig: %w", err)
		}
		fmt.Printf("Kubeconfig written to %s\n", outputFile)
	} else {
		fmt.Print(string(kubeconfig))
	}
	return nil
}

func newClusterHealthCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "health <name>",
		Short: "Get health status of a Clean Room cluster",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterHealth(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runClusterHealth(
	ctx context.Context,
	name, namespace string,
) error {
	dynClient, err := getDynamicClient()
	if err != nil {
		return err
	}

	cr, err := dynClient.Resource(clusterGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Cluster: %w", err)
	}

	infraType, _, _ := unstructured.NestedString(
		cr.Object, "spec", "infraType",
	)
	providerConfig, _, _ := unstructured.NestedMap(
		cr.Object, "spec", "providerConfig",
	)

	endpoint, cleanup, err :=
		portForwardToClusterProvider()
	if err != nil {
		return fmt.Errorf(
			"port-forwarding to provider: %w", err,
		)
	}
	defer cleanup()

	cc := client.NewClusterClient(endpoint)

	var pcRaw json.RawMessage
	if providerConfig != nil {
		pcRaw, _ = json.Marshal(providerConfig)
	}

	health, err := cc.GetHealth(ctx, name,
		&client.GetClusterInput{
			InfraType:      infraType,
			ProviderConfig: pcRaw,
		},
	)
	if err != nil {
		return fmt.Errorf("getting health: %w", err)
	}

	data, _ := json.MarshalIndent(health, "", "  ")
	fmt.Println(string(data))
	return nil
}

func newClusterWaitCmd() *cobra.Command {
	var namespace string
	var forCondition string
	var timeout string

	cmd := &cobra.Command{
		Use:   "wait <name>",
		Short: "Wait for a Cluster to reach a phase",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterWait(
				cmd.Context(), args[0],
				namespace, forCondition, timeout,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	f.StringVar(&forCondition, "for",
		"Running", "Phase to wait for")
	f.StringVar(&timeout, "timeout",
		"600s", "Timeout duration")
	return cmd
}

func runClusterWait(
	ctx context.Context,
	name, namespace, forCondition, timeout string,
) error {
	dur, err := parseTimeout(timeout)
	if err != nil {
		return err
	}

	ctx, cancel := context.WithTimeout(ctx, dur)
	defer cancel()

	dynClient, err := getDynamicClient()
	if err != nil {
		return err
	}

	fmt.Printf("Waiting for %s to reach %s...\n",
		name, forCondition)

	// Fetch the resource's creation timestamp to filter
	// out events from a prior same-named instance.
	res, err := dynClient.Resource(clusterGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Cluster: %w", err)
	}
	resCreated := res.GetCreationTimestamp().Time

	watcher, err := dynClient.Resource(clusterGVR).
		Namespace(namespace).
		Watch(ctx, metav1.ListOptions{
			FieldSelector: fmt.Sprintf("metadata.name=%s", name),
		})
	if err != nil {
		return fmt.Errorf("watching Cluster: %w", err)
	}
	defer watcher.Stop()

	// Watch events for progress messages in background.
	progressCh := watchClusterEvents(
		ctx, dynClient, name, namespace, resCreated,
	)

	lastPhase := ""
	for {
		select {
		case <-ctx.Done():
			return fmt.Errorf(
				"timed out waiting for %s to reach %s",
				name, forCondition,
			)
		case msg := <-progressCh:
			fmt.Printf("    %s\n", msg)
		case event, ok := <-watcher.ResultChan():
			if !ok {
				return fmt.Errorf("watch channel closed")
			}
			u, ok := event.Object.(*unstructured.Unstructured)
			if !ok {
				continue
			}
			phase, _, _ := unstructured.NestedString(
				u.Object, "status", "phase",
			)
			if phase != "" && phase != lastPhase {
				fmt.Printf("  Phase: %s\n", phase)
				lastPhase = phase
			}
			if phase == forCondition {
				// When waiting for Running, also check
				// the Ready condition — it may be False
				// while deferred workload profiles are
				// being applied.
				if phase == "Running" &&
					isConditionFalse(u, "Ready") {
					if lastPhase != phase {
						msg := conditionMessage(
							u, "Ready",
						)
						fmt.Printf(
							"  Phase: %s"+
								" (waiting for"+
								" Ready: %s)\n",
							phase, msg,
						)
					}
					continue
				}
				fmt.Printf(
					"%s reached %s\n", name, forCondition,
				)
				return nil
			}
			if phase == "Failed" && forCondition != "Failed" {
				msg := extractFailureMessage(u)
				return fmt.Errorf(
					"cluster entered Failed state: %s", msg,
				)
			}
		}
	}
}

// watchClusterEvents watches Kubernetes events for the given
// cluster and sends OperationProgress messages on the returned
// channel.
func watchClusterEvents(
	ctx context.Context,
	dynClient dynamic.Interface,
	name, namespace string,
	notBefore time.Time,
) <-chan string {
	ch := make(chan string, 20)

	eventsGVR := schema.GroupVersionResource{
		Group:    "",
		Version:  "v1",
		Resource: "events",
	}

	go func() {
		defer close(ch)

		eventWatcher, err := dynClient.Resource(eventsGVR).
			Namespace(namespace).
			Watch(ctx, metav1.ListOptions{
				FieldSelector: fmt.Sprintf(
					"involvedObject.name=%s,reason=OperationProgress",
					name,
				),
			})
		if err != nil {
			return
		}
		defer eventWatcher.Stop()

		lastMsg := ""
		for {
			select {
			case <-ctx.Done():
				return
			case ev, ok := <-eventWatcher.ResultChan():
				if !ok {
					return
				}
				u, ok := ev.Object.(*unstructured.Unstructured)
				if !ok {
					continue
				}
				// Skip events from a prior same-named
				// resource instance.
				lt, _, _ := unstructured.NestedString(
					u.Object, "lastTimestamp",
				)
				if lt != "" {
					if t, err := time.Parse(
						time.RFC3339, lt,
					); err == nil &&
						t.Before(notBefore) {
						continue
					}
				}
				msg, _, _ := unstructured.NestedString(
					u.Object, "message",
				)
				if msg != "" && msg != lastMsg {
					lastMsg = msg
					ch <- msg
				}
			}
		}
	}()

	return ch
}

// extractFailureMessage finds the most relevant failure reason
// from the status conditions of a Cluster resource.
func extractFailureMessage(
	u *unstructured.Unstructured,
) string {
	conditions, found, _ := unstructured.NestedSlice(
		u.Object, "status", "conditions",
	)
	if !found || len(conditions) == 0 {
		return "(no details available)"
	}

	// Priority order for finding the failure reason:
	// 1. Ready condition with status=False (set on operation
	//    failure with the actual error)
	// 2. Validated condition with status=False
	// 3. Any other condition with status=False
	type condInfo struct {
		condType string
		reason   string
		message  string
	}

	var readyCond, validatedCond, fallbackCond *condInfo
	for _, c := range conditions {
		cond, ok := c.(map[string]interface{})
		if !ok {
			continue
		}
		status, _, _ := unstructured.NestedString(
			cond, "status",
		)
		if status != "False" {
			continue
		}
		condType, _, _ := unstructured.NestedString(
			cond, "type",
		)
		reason, _, _ := unstructured.NestedString(
			cond, "reason",
		)
		message, _, _ := unstructured.NestedString(
			cond, "message",
		)
		info := &condInfo{condType, reason, message}
		switch condType {
		case "Ready":
			readyCond = info
		case "Validated":
			validatedCond = info
		default:
			if fallbackCond == nil {
				fallbackCond = info
			}
		}
	}

	// Pick the best match.
	picked := readyCond
	if picked == nil {
		picked = validatedCond
	}
	if picked == nil {
		picked = fallbackCond
	}
	if picked == nil {
		return "(no details available)"
	}

	if picked.message != "" {
		return fmt.Sprintf(
			"[%s/%s] %s",
			picked.condType, picked.reason, picked.message,
		)
	}
	if picked.reason != "" {
		return fmt.Sprintf(
			"[%s] %s", picked.condType, picked.reason,
		)
	}
	return "(no details available)"
}

// isConditionFalse returns true if the named condition exists
// with status=False on the unstructured object.
func isConditionFalse(
	u *unstructured.Unstructured, condType string,
) bool {
	conditions, found, _ := unstructured.NestedSlice(
		u.Object, "status", "conditions",
	)
	if !found {
		return false
	}
	for _, c := range conditions {
		cond, ok := c.(map[string]interface{})
		if !ok {
			continue
		}
		t, _, _ := unstructured.NestedString(
			cond, "type",
		)
		s, _, _ := unstructured.NestedString(
			cond, "status",
		)
		if t == condType && s == "False" {
			return true
		}
	}
	return false
}

// conditionMessage returns the message for the named condition,
// or an empty string if not found.
func conditionMessage(
	u *unstructured.Unstructured, condType string,
) string {
	conditions, found, _ := unstructured.NestedSlice(
		u.Object, "status", "conditions",
	)
	if !found {
		return ""
	}
	for _, c := range conditions {
		cond, ok := c.(map[string]interface{})
		if !ok {
			continue
		}
		t, _, _ := unstructured.NestedString(
			cond, "type",
		)
		if t == condType {
			msg, _, _ := unstructured.NestedString(
				cond, "message",
			)
			return msg
		}
	}
	return ""
}

// portForwardToClusterProvider opens a port-forward to the
// cluster-provider-client service and returns the localhost
// endpoint URL and a cleanup function to stop the
// port-forward.
func portForwardToClusterProvider() (
	string, func(), error,
) {
	return portForwardToService(
		"svc/cluster-provider-client", 18480, 8080,
	)
}

// portForwardToCcfProvider opens a port-forward to the
// ccf-provider-client service and returns the localhost
// endpoint URL and a cleanup function.
func portForwardToCcfProvider() (
	string, func(), error,
) {
	return portForwardToService(
		"svc/ccf-provider-client", 18481, 8080,
	)
}

// portForwardToService opens a kubectl port-forward to the
// given target (pod name or svc/name) in the operator
// namespace and returns a localhost endpoint URL and a
// cleanup function.
func portForwardToService(
	target string,
	localPort, remotePort int,
) (string, func(), error) {
	pf, port, err := startPortForwardToPort(
		operatorNamespace, target, localPort, remotePort,
	)
	if err != nil {
		return "", nil, fmt.Errorf(
			"port-forwarding to %s: %w", target, err,
		)
	}
	endpoint := fmt.Sprintf(
		"http://localhost:%d", port,
	)
	cleanup := func() {
		if pf.Process != nil {
			_ = pf.Process.Kill()
		}
	}
	return endpoint, cleanup, nil
}

func getCcfProviderEndpoint(
	ctx context.Context,
	namespace string,
) (string, error) {
	dynClient, err := getDynamicClient()
	if err != nil {
		return "", err
	}

	cmGVR := schema.GroupVersionResource{
		Group:    "",
		Version:  "v1",
		Resource: "configmaps",
	}

	cm, err := dynClient.Resource(cmGVR).
		Namespace(operatorNamespace).
		Get(ctx, "cleanroom-operator-config", metav1.GetOptions{})
	if err != nil {
		return "", fmt.Errorf(
			"getting operator config: %w", err,
		)
	}

	data, _, _ := unstructured.NestedStringMap(
		cm.Object, "data",
	)
	endpoint, ok := data["CCF_PROVIDER_CLIENT_ENDPOINT"]
	if !ok || endpoint == "" {
		return "", fmt.Errorf(
			"CCF_PROVIDER_CLIENT_ENDPOINT not found in ConfigMap",
		)
	}
	return endpoint, nil
}

func parseTimeout(s string) (time.Duration, error) {
	return time.ParseDuration(s)
}
