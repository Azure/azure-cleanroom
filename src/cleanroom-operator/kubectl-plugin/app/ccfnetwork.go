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
)

var ccfNetworkGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "ccfnetworks",
}

type ccfNetworkCreateOpts struct {
	infraType      string
	nodeCount      int
	membersFile    string
	nodeLogLevel   string
	providerConfig string
	namespace      string
	wait           bool
	timeout        string
}

func newCcfNetworkCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "ccf-network",
		Short: "Manage CCF networks",
	}

	cmd.AddCommand(newCcfNetworkCreateCmd())
	cmd.AddCommand(newCcfNetworkGetCmd())
	cmd.AddCommand(newCcfNetworkDeleteCmd())
	cmd.AddCommand(newCcfNetworkListCmd())
	cmd.AddCommand(newCcfNetworkWaitCmd())
	return cmd
}

func newCcfNetworkCreateCmd() *cobra.Command {
	o := &ccfNetworkCreateOpts{}

	cmd := &cobra.Command{
		Use:   "create <name>",
		Short: "Create a CcfNetwork custom resource",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runCcfNetworkCreate(
				cmd.Context(), args[0], o,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(&o.infraType, "infra-type", "virtual",
		"Infrastructure type (virtual, caci)")
	f.IntVar(&o.nodeCount, "node-count", 1,
		"Number of CCF nodes")
	f.StringVar(&o.membersFile, "members-file", "",
		"Path to JSON file with member definitions")
	_ = cmd.MarkFlagRequired("members-file")
	f.StringVar(&o.nodeLogLevel, "node-log-level", "",
		"CCF node log level (Trace, Debug, Info, Fail, Fatal)")
	f.StringVar(&o.providerConfig, "provider-config", "",
		"Path to provider config JSON file")
	f.StringVar(&o.namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	f.BoolVar(&o.wait, "wait", false,
		"Wait for network to reach Running state")
	f.StringVar(&o.timeout, "timeout", "600s",
		"Timeout when using --wait")
	return cmd
}

func runCcfNetworkCreate(
	ctx context.Context,
	name string,
	o *ccfNetworkCreateOpts,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	// Read members from JSON file.
	membersData, err := os.ReadFile(o.membersFile)
	if err != nil {
		return fmt.Errorf("reading members file: %w", err)
	}

	var members []interface{}
	if err := json.Unmarshal(membersData, &members); err != nil {
		return fmt.Errorf(
			"parsing members JSON: %w", err,
		)
	}

	// Resolve file paths in certificate and encryptionPublicKey
	// fields to their PEM content.
	for _, m := range members {
		mMap, ok := m.(map[string]interface{})
		if !ok {
			continue
		}
		if err := resolveFileField(mMap, "certificate"); err != nil {
			return err
		}
		if err := resolveFileField(
			mMap, "encryptionPublicKey",
		); err != nil {
			return err
		}
	}

	spec := map[string]interface{}{
		"infraType": o.infraType,
		"nodeCount": int64(o.nodeCount),
		"members":   members,
	}

	if o.nodeLogLevel != "" {
		spec["nodeLogLevel"] = o.nodeLogLevel
	}

	if o.providerConfig != "" {
		data, err := os.ReadFile(o.providerConfig)
		if err != nil {
			return fmt.Errorf(
				"reading provider config: %w", err,
			)
		}
		var pc map[string]interface{}
		if err := json.Unmarshal(data, &pc); err != nil {
			return fmt.Errorf(
				"parsing provider config JSON: %w", err,
			)
		}
		spec["providerConfig"] = pc
	}

	obj := &unstructured.Unstructured{
		Object: map[string]interface{}{
			"apiVersion": "cleanroom.azure.com/v1alpha1",
			"kind":       "CcfNetwork",
			"metadata": map[string]interface{}{
				"name":      name,
				"namespace": o.namespace,
			},
			"spec": spec,
		},
	}

	result, err := client.Resource(ccfNetworkGVR).
		Namespace(o.namespace).
		Apply(ctx, name, obj, metav1.ApplyOptions{
			FieldManager: "kubectl-cleanroom",
		})
	if err != nil {
		return fmt.Errorf("applying CcfNetwork: %w", err)
	}

	fmt.Printf("CcfNetwork %q created in namespace %q\n",
		result.GetName(), result.GetNamespace())

	if o.wait {
		return runCcfNetworkWait(
			ctx, name, o.namespace, "Running", o.timeout,
		)
	}

	return nil
}

func newCcfNetworkGetCmd() *cobra.Command {
	var namespace string
	var output string

	cmd := &cobra.Command{
		Use:   "get <name>",
		Short: "Get a CcfNetwork",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runCcfNetworkGet(
				cmd.Context(), args[0], namespace, output,
			)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	cmd.Flags().StringVarP(&output, "output", "o",
		"", "Output format (json, yaml, wide)")
	return cmd
}

func runCcfNetworkGet(
	ctx context.Context,
	name, namespace, output string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	result, err := client.Resource(ccfNetworkGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting CcfNetwork: %w", err)
	}

	switch output {
	case "json":
		data, _ := json.MarshalIndent(result.Object, "", "  ")
		fmt.Println(string(data))
	case "yaml":
		data, _ := json.Marshal(result.Object)
		fmt.Println(string(data))
	default:
		phase, _, _ := unstructured.NestedString(
			result.Object, "status", "phase",
		)
		infraType, _, _ := unstructured.NestedString(
			result.Object, "spec", "infraType",
		)
		endpoint, _, _ := unstructured.NestedString(
			result.Object, "status", "endpoint",
		)
		fmt.Printf("NAME\tPHASE\tINFRA-TYPE\tENDPOINT\n")
		fmt.Printf("%s\t%s\t%s\t%s\n",
			name, phase, infraType, endpoint)
	}
	return nil
}

func newCcfNetworkDeleteCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "delete <name>",
		Short: "Delete a CcfNetwork",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runCcfNetworkDelete(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runCcfNetworkDelete(
	ctx context.Context,
	name, namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if err := client.Resource(ccfNetworkGVR).
		Namespace(namespace).
		Delete(ctx, name, metav1.DeleteOptions{}); err != nil {
		return fmt.Errorf("deleting CcfNetwork: %w", err)
	}

	fmt.Printf("CcfNetwork %q deleted\n", name)
	return nil
}

func newCcfNetworkListCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "list",
		Short: "List CcfNetworks",
		RunE: func(cmd *cobra.Command, args []string) error {
			return runCcfNetworkList(cmd.Context(), namespace)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runCcfNetworkList(
	ctx context.Context, namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	list, err := client.Resource(ccfNetworkGVR).
		Namespace(namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return fmt.Errorf("listing CcfNetworks: %w", err)
	}

	fmt.Printf("NAME\tPHASE\tINFRA-TYPE\tENDPOINT\n")
	for _, item := range list.Items {
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		infraType, _, _ := unstructured.NestedString(
			item.Object, "spec", "infraType",
		)
		endpoint, _, _ := unstructured.NestedString(
			item.Object, "status", "endpoint",
		)
		fmt.Printf("%s\t%s\t%s\t%s\n",
			item.GetName(), phase, infraType, endpoint)
	}
	return nil
}

func newCcfNetworkWaitCmd() *cobra.Command {
	var namespace string
	var forCondition string
	var timeout string

	cmd := &cobra.Command{
		Use:   "wait <name>",
		Short: "Wait for a CcfNetwork to reach a phase",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runCcfNetworkWait(
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

func runCcfNetworkWait(
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
	res, err := dynClient.Resource(ccfNetworkGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting CcfNetwork: %w", err)
	}
	resCreated := res.GetCreationTimestamp().Time

	watcher, err := dynClient.Resource(ccfNetworkGVR).
		Namespace(namespace).
		Watch(ctx, metav1.ListOptions{
			FieldSelector: fmt.Sprintf(
				"metadata.name=%s", name,
			),
		})
	if err != nil {
		return fmt.Errorf("watching CcfNetwork: %w", err)
	}
	defer watcher.Stop()

	// Watch events for progress messages in background.
	progressCh := watchCcfNetworkEvents(
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
				fmt.Printf(
					"%s reached %s\n", name, forCondition,
				)
				return nil
			}
			if phase == "Failed" && forCondition != "Failed" {
				msg := extractCcfFailureMessage(u)
				return fmt.Errorf(
					"CCF network entered Failed state: %s",
					msg,
				)
			}
		}
	}
}

func watchCcfNetworkEvents(
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
					"involvedObject.name=%s,"+
						"reason=OperationProgress",
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

func extractCcfFailureMessage(
	u *unstructured.Unstructured,
) string {
	conditions, found, _ := unstructured.NestedSlice(
		u.Object, "status", "conditions",
	)
	if !found || len(conditions) == 0 {
		return "(no details available)"
	}

	type condInfo struct {
		condType string
		reason   string
		message  string
	}

	var readyCond, fallbackCond *condInfo
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
		default:
			if fallbackCond == nil {
				fallbackCond = info
			}
		}
	}

	picked := readyCond
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

// resolveFileField checks if the given field in the map looks like
// a file path (starts with "/" or ".") and if so reads the file
// content and replaces the value. This allows members.json to use
// file paths for certificate/key fields.
func resolveFileField(
	m map[string]interface{}, field string,
) error {
	val, ok := m[field].(string)
	if !ok || val == "" {
		return nil
	}
	if val[0] != '/' && val[0] != '.' {
		return nil
	}
	data, err := os.ReadFile(val)
	if err != nil {
		return fmt.Errorf(
			"reading %s file %q: %w", field, val, err,
		)
	}
	m[field] = string(data)
	return nil
}
