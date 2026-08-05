package app

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"strings"
	"time"

	"github.com/spf13/cobra"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/client-go/dynamic"
	"k8s.io/client-go/tools/clientcmd"

	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
	"k8s.io/apimachinery/pkg/runtime/schema"
)

var clusterGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "clusters",
}

type clusterCreateOpts struct {
	infraType                     string
	enableObservability           bool
	enableMonitoring              bool
	enableAnalytics               bool
	analyticsConfigUrl            string
	analyticsConfigUrlCaCert      string
	analyticsSecurityPolicyOption string
	enableKServeInferencing       bool
	kserveConfigUrl               string
	kserveConfigUrlCaCert         string
	kserveSecurityPolicyOption    string
	enableFlexNode                bool
	flexNodeCount                 int
	flexNodeMode                  string
	flexVmSize                    string
	flexPolicyCertFile            string
	flexInsecure                  bool
	aadAdminGroupIds              []string
	providerConfigFile            string
	subscription                  string
	resourceGroup                 string
	location                      string
	tenantId                      string
	kindClusterName               string
	aksClusterName                string
	namespace                     string
	wait                          bool
	timeout                       string
}

func newClusterCreateCmd() *cobra.Command {
	o := &clusterCreateOpts{}

	cmd := &cobra.Command{
		Use:   "create <name>",
		Short: "Create a Cluster custom resource",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterCreate(cmd.Context(), args[0], o)
		},
	}

	f := cmd.Flags()
	f.StringVar(&o.infraType, "infra-type", "",
		"Infrastructure type (virtual or aks)")
	_ = cmd.MarkFlagRequired("infra-type")
	f.BoolVar(&o.enableObservability, "enable-observability",
		false, "Enable observability")
	f.BoolVar(&o.enableMonitoring, "enable-monitoring",
		false, "Enable monitoring")
	f.BoolVar(&o.enableAnalytics, "enable-analytics-workload",
		false, "Enable analytics workload")
	f.StringVar(&o.analyticsConfigUrl,
		"analytics-workload-config-url", "",
		"Analytics workload configuration URL")
	f.StringVar(&o.analyticsConfigUrlCaCert,
		"analytics-workload-config-url-ca-cert", "",
		"CA cert for analytics config URL")
	f.StringVar(&o.analyticsSecurityPolicyOption,
		"analytics-workload-security-policy-creation-option",
		"", "Security policy creation option")
	f.BoolVar(&o.enableKServeInferencing,
		"enable-kserve-inferencing-workload",
		false, "Enable KServe inferencing")
	f.StringVar(&o.kserveConfigUrl,
		"kserve-inferencing-workload-config-url", "",
		"KServe inferencing configuration URL")
	f.StringVar(&o.kserveConfigUrlCaCert,
		"kserve-inferencing-workload-config-url-ca-cert", "",
		"CA cert for KServe config URL")
	f.StringVar(&o.kserveSecurityPolicyOption,
		"kserve-inferencing-workload-security-policy-creation-option",
		"", "Security policy creation option")
	f.BoolVar(&o.enableFlexNode, "enable-flex-node",
		false, "Enable flex nodes")
	f.StringVar(&o.flexNodeMode, "flex-node-mode",
		"manual",
		"Flex node provisioning mode (manual|auto)")
	f.IntVar(&o.flexNodeCount, "flex-node-count",
		2, "Number of flex nodes")
	f.StringVar(&o.flexVmSize, "flex-node-vm-size",
		"", "VM size for flex nodes")
	f.StringVar(&o.flexPolicyCertFile,
		"flex-node-policy-signing-cert", "",
		"Path to policy signing certificate PEM file")
	f.BoolVar(&o.flexInsecure, "flex-node-insecure",
		false, "Disable security checks on flex nodes")
	f.StringSliceVar(&o.aadAdminGroupIds,
		"aad-admin-group-ids", nil,
		"AAD admin group object IDs")
	f.StringVar(&o.providerConfigFile,
		"provider-config", "",
		"Path to provider config JSON file")
	f.StringVar(&o.subscription, "subscription",
		"", "Azure subscription ID (auto-detected from az CLI)")
	f.StringVar(&o.resourceGroup, "resource-group",
		"", "Azure resource group name")
	f.StringVar(&o.location, "location",
		"", "Azure location/region")
	f.StringVar(&o.tenantId, "tenant-id",
		"", "Azure tenant ID (auto-detected from az CLI)")
	f.StringVar(&o.kindClusterName, "kind-cluster-name",
		"", "Name of an existing kind cluster to target "+
			"(--infra-type virtual)")
	f.StringVar(&o.aksClusterName, "aks-cluster-name",
		"", "Name of an existing AKS cluster to target "+
			"(--infra-type aks)")
	f.StringVar(&o.namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	f.BoolVar(&o.wait, "wait",
		false, "Wait for cluster to reach Running state")
	f.StringVar(&o.timeout, "timeout",
		"600s", "Timeout when using --wait")
	return cmd
}

func runClusterCreate(
	ctx context.Context,
	name string,
	o *clusterCreateOpts,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	spec := map[string]interface{}{
		"infraType": o.infraType,
	}

	if o.enableObservability {
		spec["observabilityProfile"] = map[string]interface{}{
			"enabled": true,
		}
	}

	if o.enableMonitoring {
		spec["monitoringProfile"] = map[string]interface{}{
			"enabled": true,
		}
	}

	if o.enableAnalytics {
		analytics := map[string]interface{}{
			"enabled": true,
		}
		if o.analyticsConfigUrl != "" {
			analytics["configurationUrl"] = o.analyticsConfigUrl
		}
		if o.analyticsConfigUrlCaCert != "" {
			analytics["configurationUrlCaCert"] =
				o.analyticsConfigUrlCaCert
		}
		if o.analyticsSecurityPolicyOption != "" {
			analytics["securityPolicyCreationOption"] =
				o.analyticsSecurityPolicyOption
		}
		spec["analyticsWorkloadProfile"] = analytics
	}

	if o.enableKServeInferencing {
		kserve := map[string]interface{}{
			"enabled": true,
		}
		if o.kserveConfigUrl != "" {
			kserve["configurationUrl"] = o.kserveConfigUrl
		}
		if o.kserveConfigUrlCaCert != "" {
			kserve["configurationUrlCaCert"] =
				o.kserveConfigUrlCaCert
		}
		if o.kserveSecurityPolicyOption != "" {
			kserve["securityPolicyCreationOption"] =
				o.kserveSecurityPolicyOption
		}
		spec["inferencingWorkloadProfile"] = map[string]interface{}{
			"kserveProfile": kserve,
		}
	}

	if o.enableFlexNode {
		flex := map[string]interface{}{
			"enabled":   true,
			"mode":      o.flexNodeMode,
			"nodeCount": int64(o.flexNodeCount),
		}
		if o.flexVmSize != "" {
			flex["vmSize"] = o.flexVmSize
		}
		if o.flexInsecure {
			flex["insecure"] = true
		}
		if o.flexPolicyCertFile != "" {
			certData, err := os.ReadFile(o.flexPolicyCertFile)
			if err != nil {
				return fmt.Errorf(
					"reading policy cert file: %w", err,
				)
			}
			flex["policySigningCertPem"] = string(certData)
		}
		spec["flexNodeProfile"] = flex
	}

	if len(o.aadAdminGroupIds) > 0 {
		spec["aadProfile"] = map[string]interface{}{
			"enabled":             true,
			"adminGroupObjectIds": o.aadAdminGroupIds,
		}
	}

	if o.providerConfigFile != "" {
		data, err := os.ReadFile(o.providerConfigFile)
		if err != nil {
			return fmt.Errorf(
				"reading provider config: %w", err,
			)
		}
		var providerConfig map[string]interface{}
		if err := json.Unmarshal(data, &providerConfig); err != nil {
			return fmt.Errorf(
				"parsing provider config JSON: %w", err,
			)
		}
		spec["providerConfig"] = providerConfig
	} else if o.infraType == "aks" {
		pc, err := buildAksProviderConfig(o)
		if err != nil {
			return err
		}
		spec["providerConfig"] = pc
	}

	// Inject cluster name overrides so the provider
	// targets an existing workload cluster by name.
	if o.kindClusterName != "" || o.aksClusterName != "" {
		pc, _ := spec["providerConfig"].(map[string]interface{})
		if pc == nil {
			pc = map[string]interface{}{}
		}
		if o.kindClusterName != "" {
			pc["kindClusterName"] = o.kindClusterName
		}
		if o.aksClusterName != "" {
			pc["aksClusterName"] = o.aksClusterName
		}
		spec["providerConfig"] = pc
	}

	obj := &unstructured.Unstructured{
		Object: map[string]interface{}{
			"apiVersion": "cleanroom.azure.com/v1alpha1",
			"kind":       "Cluster",
			"metadata": map[string]interface{}{
				"name":      name,
				"namespace": o.namespace,
			},
			"spec": spec,
		},
	}

	result, err := client.Resource(clusterGVR).
		Namespace(o.namespace).
		Apply(ctx, name, obj, metav1.ApplyOptions{
			FieldManager: "kubectl-cleanroom",
		})
	if err != nil {
		return fmt.Errorf("applying Cluster: %w", err)
	}

	fmt.Printf("Cluster %q created in namespace %q\n",
		result.GetName(), result.GetNamespace())

	if o.wait {
		return runClusterWait(
			ctx, name, o.namespace, "Running", o.timeout,
		)
	}

	return nil
}

func newClusterUpdateCmd() *cobra.Command {
	o := &clusterCreateOpts{}

	cmd := &cobra.Command{
		Use:   "update <name>",
		Short: "Update a Cluster custom resource",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterUpdate(ctx(cmd), args[0], o, cmd)
		},
	}

	f := cmd.Flags()
	f.BoolVar(&o.enableObservability, "enable-observability",
		false, "Enable observability")
	f.BoolVar(&o.enableMonitoring, "enable-monitoring",
		false, "Enable monitoring")
	f.BoolVar(&o.enableAnalytics, "enable-analytics-workload",
		false, "Enable analytics workload")
	f.StringVar(&o.analyticsConfigUrl,
		"analytics-workload-config-url", "",
		"Analytics workload configuration URL")
	f.StringVar(&o.analyticsConfigUrlCaCert,
		"analytics-workload-config-url-ca-cert", "",
		"CA cert for analytics config URL")
	f.StringVar(&o.analyticsSecurityPolicyOption,
		"analytics-workload-security-policy-creation-option",
		"", "Security policy creation option")
	f.BoolVar(&o.enableKServeInferencing,
		"enable-kserve-inferencing-workload",
		false, "Enable KServe inferencing")
	f.StringVar(&o.kserveConfigUrl,
		"kserve-inferencing-workload-config-url", "",
		"KServe inferencing configuration URL")
	f.StringVar(&o.kserveConfigUrlCaCert,
		"kserve-inferencing-workload-config-url-ca-cert", "",
		"CA cert for KServe config URL")
	f.StringVar(&o.kserveSecurityPolicyOption,
		"kserve-inferencing-workload-security-policy-creation-option",
		"", "Security policy creation option")
	f.BoolVar(&o.enableFlexNode, "enable-flex-node",
		false, "Enable flex nodes")
	f.StringVar(&o.flexNodeMode, "flex-node-mode",
		"manual",
		"Flex node provisioning mode (manual|auto)")
	f.IntVar(&o.flexNodeCount, "flex-node-count",
		2, "Number of flex nodes")
	f.StringVar(&o.flexVmSize, "flex-node-vm-size",
		"", "VM size for flex nodes")
	f.StringVar(&o.flexPolicyCertFile,
		"flex-node-policy-signing-cert", "",
		"Path to policy signing certificate PEM file")
	f.BoolVar(&o.flexInsecure, "flex-node-insecure",
		false, "Disable security checks on flex nodes")
	f.StringSliceVar(&o.aadAdminGroupIds,
		"aad-admin-group-ids", nil,
		"AAD admin group object IDs")
	f.StringVar(&o.providerConfigFile,
		"provider-config", "",
		"Path to provider config JSON file")
	f.StringVar(&o.subscription, "subscription",
		"", "Azure subscription ID (auto-detected from az CLI)")
	f.StringVar(&o.resourceGroup, "resource-group",
		"", "Azure resource group name")
	f.StringVar(&o.location, "location",
		"", "Azure location/region")
	f.StringVar(&o.tenantId, "tenant-id",
		"", "Azure tenant ID (auto-detected from az CLI)")
	f.StringVar(&o.namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runClusterUpdate(
	ctx context.Context,
	name string,
	o *clusterCreateOpts,
	cmd *cobra.Command,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	// Get existing resource.
	existing, err := client.Resource(clusterGVR).
		Namespace(o.namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Cluster: %w", err)
	}

	spec, _, _ := unstructured.NestedMap(
		existing.Object, "spec",
	)
	if spec == nil {
		spec = map[string]interface{}{}
	}

	// Merge only flags that were explicitly set.
	if cmd.Flags().Changed("enable-observability") {
		spec["observabilityProfile"] = map[string]interface{}{
			"enabled": o.enableObservability,
		}
	}

	if cmd.Flags().Changed("enable-monitoring") {
		spec["monitoringProfile"] = map[string]interface{}{
			"enabled": o.enableMonitoring,
		}
	}

	if cmd.Flags().Changed("enable-analytics-workload") {
		analytics := getOrCreateMap(spec, "analyticsWorkloadProfile")
		analytics["enabled"] = o.enableAnalytics
		if o.analyticsConfigUrl != "" {
			analytics["configurationUrl"] = o.analyticsConfigUrl
		}
		if o.analyticsConfigUrlCaCert != "" {
			analytics["configurationUrlCaCert"] =
				o.analyticsConfigUrlCaCert
		}
		if o.analyticsSecurityPolicyOption != "" {
			analytics["securityPolicyCreationOption"] =
				o.analyticsSecurityPolicyOption
		}
		spec["analyticsWorkloadProfile"] = analytics
	}

	if cmd.Flags().Changed("enable-kserve-inferencing-workload") {
		inferencing := getOrCreateMap(
			spec, "inferencingWorkloadProfile",
		)
		kserve := getOrCreateMap(inferencing, "kserveProfile")
		kserve["enabled"] = o.enableKServeInferencing
		if o.kserveConfigUrl != "" {
			kserve["configurationUrl"] = o.kserveConfigUrl
		}
		if o.kserveConfigUrlCaCert != "" {
			kserve["configurationUrlCaCert"] =
				o.kserveConfigUrlCaCert
		}
		if o.kserveSecurityPolicyOption != "" {
			kserve["securityPolicyCreationOption"] =
				o.kserveSecurityPolicyOption
		}
		inferencing["kserveProfile"] = kserve
		spec["inferencingWorkloadProfile"] = inferencing
	}

	if cmd.Flags().Changed("enable-flex-node") {
		flex := getOrCreateMap(spec, "flexNodeProfile")
		flex["enabled"] = o.enableFlexNode
		if cmd.Flags().Changed("flex-node-mode") {
			flex["mode"] = o.flexNodeMode
		}
		flex["nodeCount"] = int64(o.flexNodeCount)
		if o.flexVmSize != "" {
			flex["vmSize"] = o.flexVmSize
		}
		if o.flexInsecure {
			flex["insecure"] = true
		}
		if o.flexPolicyCertFile != "" {
			certData, err := os.ReadFile(o.flexPolicyCertFile)
			if err != nil {
				return fmt.Errorf(
					"reading policy cert file: %w", err,
				)
			}
			flex["policySigningCertPem"] = string(certData)
		}
		spec["flexNodeProfile"] = flex
	}

	if cmd.Flags().Changed("aad-admin-group-ids") {
		spec["aadProfile"] = map[string]interface{}{
			"enabled":             true,
			"adminGroupObjectIds": o.aadAdminGroupIds,
		}
	}

	if cmd.Flags().Changed("provider-config") &&
		o.providerConfigFile != "" {
		data, err := os.ReadFile(o.providerConfigFile)
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
	} else if azureFlagsChanged(cmd) {
		pc := getOrCreateMap(spec, "providerConfig")
		if cmd.Flags().Changed("subscription") {
			pc["subscriptionId"] = o.subscription
		}
		if cmd.Flags().Changed("resource-group") {
			pc["resourceGroupName"] = o.resourceGroup
		}
		if cmd.Flags().Changed("location") {
			pc["location"] = o.location
		}
		if cmd.Flags().Changed("tenant-id") {
			pc["tenantId"] = o.tenantId
		}
		spec["providerConfig"] = pc
	}

	existing.Object["spec"] = spec

	result, err := client.Resource(clusterGVR).
		Namespace(o.namespace).
		Update(ctx, existing, metav1.UpdateOptions{
			FieldManager: "kubectl-cleanroom",
		})
	if err != nil {
		return fmt.Errorf("updating Cluster: %w", err)
	}

	fmt.Printf("Cluster %q updated\n", result.GetName())
	return nil
}

func newClusterGetCmd() *cobra.Command {
	var namespace string
	var output string

	cmd := &cobra.Command{
		Use:   "get <name>",
		Short: "Get a Cluster",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterGet(
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

func runClusterGet(
	ctx context.Context,
	name, namespace, output string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	result, err := client.Resource(clusterGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Cluster: %w", err)
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
		fmt.Printf("NAME\tPHASE\tINFRA-TYPE\n")
		fmt.Printf("%s\t%s\t%s\n", name, phase, infraType)
	}
	return nil
}

func newClusterDeleteCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "delete <name>",
		Short: "Delete a Cluster",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterDelete(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runClusterDelete(
	ctx context.Context,
	name, namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if err := client.Resource(clusterGVR).
		Namespace(namespace).
		Delete(ctx, name, metav1.DeleteOptions{}); err != nil {
		return fmt.Errorf("deleting Cluster: %w", err)
	}

	fmt.Printf("Cluster %q deleted\n", name)
	return nil
}

func newClusterListCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "list",
		Short: "List Clusters",
		RunE: func(cmd *cobra.Command, args []string) error {
			return runClusterList(cmd.Context(), namespace)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runClusterList(ctx context.Context, namespace string) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	list, err := client.Resource(clusterGVR).
		Namespace(namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return fmt.Errorf("listing Clusters: %w", err)
	}

	fmt.Printf("NAME\tPHASE\tINFRA-TYPE\n")
	for _, item := range list.Items {
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		infraType, _, _ := unstructured.NestedString(
			item.Object, "spec", "infraType",
		)
		fmt.Printf("%s\t%s\t%s\n",
			item.GetName(), phase, infraType)
	}
	return nil
}

func newClusterReconcileCmd() *cobra.Command {
	var namespace string
	var noWait bool
	var timeout string

	cmd := &cobra.Command{
		Use:     "reconcile <name>",
		Aliases: []string{"retry"},
		Short:   "Trigger reconciliation of a Cluster",
		Long: `Force the operator to re-evaluate the
Cluster. Works on any phase — not just Failed. Sets
the reconcile.cleanroom.azure.com/requestedAt
annotation and waits for the controller to
acknowledge it.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runClusterReconcile(
				cmd.Context(), args[0],
				namespace, !noWait, timeout,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(),
		"Kubernetes namespace")
	f.BoolVar(&noWait, "no-wait", false,
		"Do not wait for cluster to reach Running "+
			"state after triggering reconciliation")
	f.StringVar(&timeout, "timeout", "900s",
		"Timeout when waiting")
	return cmd
}

func runClusterReconcile(
	ctx context.Context,
	name, namespace string,
	wait bool,
	timeout string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	cluster, err := client.Resource(clusterGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Cluster: %w", err)
	}

	phase, _, _ := unstructured.NestedString(
		cluster.Object, "status", "phase",
	)

	// Set reconcile timestamp annotation.
	requestedAt := time.Now().UTC().Format(
		time.RFC3339Nano,
	)
	annotations := cluster.GetAnnotations()
	if annotations == nil {
		annotations = map[string]string{}
	}
	annotations["reconcile.cleanroom.azure.com/requestedAt"] = requestedAt
	cluster.SetAnnotations(annotations)

	_, err = client.Resource(clusterGVR).
		Namespace(namespace).
		Update(ctx, cluster, metav1.UpdateOptions{})
	if err != nil {
		return fmt.Errorf(
			"setting reconcile annotation: %w", err,
		)
	}

	fmt.Printf(
		"► reconciliation triggered for "+
			"Cluster %q (was: %s)\n",
		name, phase,
	)

	if wait {
		// Poll until the controller acknowledges the
		// reconcile request before starting the watch.
		dur, err := parseTimeout(timeout)
		if err != nil {
			return err
		}
		waitCtx, cancel := context.WithTimeout(
			ctx, dur,
		)
		defer cancel()

		ticker := time.NewTicker(2 * time.Second)
		defer ticker.Stop()

		for {
			select {
			case <-waitCtx.Done():
				return fmt.Errorf(
					"timed out waiting for " +
						"controller to acknowledge " +
						"reconcile request",
				)
			case <-ticker.C:
				latest, err := client.Resource(
					clusterGVR,
				).Namespace(namespace).Get(
					waitCtx, name,
					metav1.GetOptions{},
				)
				if err != nil {
					continue
				}
				handled, _, _ :=
					unstructured.NestedString(
						latest.Object, "status",
						"lastHandledReconcileAt",
					)
				if handled == requestedAt {
					// Controller has acknowledged;
					// start the condition watch.
					return runClusterWait(
						ctx, name, namespace,
						"Running", timeout,
					)
				}
			}
		}
	}

	return nil
}

func getDynamicClient() (dynamic.Interface, error) {
	loadingRules := clientcmd.NewDefaultClientConfigLoadingRules()
	if kubeconfigPath != "" {
		loadingRules.ExplicitPath = kubeconfigPath
	}
	configOverrides := &clientcmd.ConfigOverrides{}
	kubeConfig := clientcmd.NewNonInteractiveDeferredLoadingClientConfig(
		loadingRules, configOverrides,
	)

	config, err := kubeConfig.ClientConfig()
	if err != nil {
		return nil, fmt.Errorf("loading kubeconfig: %w", err)
	}

	return dynamic.NewForConfig(config)
}

// defaultNamespaceFromKubeconfig returns the namespace from the
// current kubeconfig context, falling back to "default".
func defaultNamespaceFromKubeconfig() string {
	loadingRules := clientcmd.NewDefaultClientConfigLoadingRules()
	if kubeconfigPath != "" {
		loadingRules.ExplicitPath = kubeconfigPath
	}
	kubeConfig := clientcmd.NewNonInteractiveDeferredLoadingClientConfig(
		loadingRules, &clientcmd.ConfigOverrides{},
	)
	ns, _, err := kubeConfig.Namespace()
	if err != nil || ns == "" {
		return "default"
	}
	return ns
}

func getOrCreateMap(
	parent map[string]interface{},
	key string,
) map[string]interface{} {
	if v, ok := parent[key]; ok {
		if m, ok := v.(map[string]interface{}); ok {
			return m
		}
	}
	return map[string]interface{}{}
}

func ctx(cmd *cobra.Command) context.Context {
	return cmd.Context()
}

// buildAksProviderConfig assembles providerConfig for AKS from
// CLI flags, auto-detecting subscription and tenant from az CLI
// when not explicitly provided. Requires --resource-group and
// --location.
func buildAksProviderConfig(
	o *clusterCreateOpts,
) (map[string]interface{}, error) {
	sub := o.subscription
	tenant := o.tenantId

	// Auto-detect from az CLI if not provided.
	if sub == "" || tenant == "" {
		azCtx, err := getAzureContext()
		if err != nil {
			missing := []string{}
			if o.resourceGroup == "" {
				missing = append(missing, "--resource-group")
			}
			if o.location == "" {
				missing = append(missing, "--location")
			}
			if sub == "" {
				missing = append(missing, "--subscription")
			}
			if tenant == "" {
				missing = append(missing, "--tenant-id")
			}
			return nil, fmt.Errorf(
				"AKS clusters require Azure config; "+
					"provide %s or log in with "+
					"'az login': %w",
				joinFlags(missing), err,
			)
		}
		if sub == "" {
			sub = azCtx.subscriptionId
		}
		if tenant == "" {
			tenant = azCtx.tenantId
		}
	}

	if o.resourceGroup == "" || o.location == "" {
		if o.resourceGroup == "" {
			return nil, fmt.Errorf(
				"AKS clusters require --resource-group",
			)
		}
		// Try to infer location from the resource group.
		rgLocation, err := getResourceGroupLocation(
			o.resourceGroup,
		)
		if err != nil {
			return nil, fmt.Errorf(
				"AKS clusters require --location "+
					"(could not detect from resource "+
					"group %q: %w)",
				o.resourceGroup, err,
			)
		}
		o.location = rgLocation
		fmt.Printf(
			"Using location %q from resource group %q\n",
			o.location, o.resourceGroup,
		)
	}

	return map[string]interface{}{
		"subscriptionId":    sub,
		"resourceGroupName": o.resourceGroup,
		"location":          o.location,
		"tenantId":          tenant,
	}, nil
}

type azureContext struct {
	subscriptionId   string
	subscriptionName string
	tenantId         string
	tenantName       string
}

// getAzureContext runs 'az account show' to get the current
// subscription and tenant.
func getAzureContext() (*azureContext, error) {
	out, err := exec.Command(
		"az", "account", "show", "--output", "json",
	).Output()
	if err != nil {
		return nil, fmt.Errorf(
			"running 'az account show': %w", err,
		)
	}

	var account struct {
		ID                string `json:"id"`
		Name              string `json:"name"`
		TenantID          string `json:"tenantId"`
		TenantDisplayName string `json:"tenantDisplayName"`
	}
	if err := json.Unmarshal(out, &account); err != nil {
		return nil, fmt.Errorf(
			"parsing az account output: %w", err,
		)
	}

	return &azureContext{
		subscriptionId:   account.ID,
		subscriptionName: account.Name,
		tenantId:         account.TenantID,
		tenantName:       account.TenantDisplayName,
	}, nil
}

// getResourceGroupLocation runs 'az group show' to get the
// location of an existing resource group.
func getResourceGroupLocation(rg string) (string, error) {
	out, err := exec.Command(
		"az", "group", "show",
		"--name", rg,
		"--query", "location",
		"--output", "tsv",
	).Output()
	if err != nil {
		return "", fmt.Errorf(
			"running 'az group show': %w", err,
		)
	}
	location := strings.TrimSpace(string(out))
	if location == "" {
		return "", fmt.Errorf("empty location returned")
	}
	return location, nil
}

func azureFlagsChanged(cmd *cobra.Command) bool {
	return cmd.Flags().Changed("subscription") ||
		cmd.Flags().Changed("resource-group") ||
		cmd.Flags().Changed("location") ||
		cmd.Flags().Changed("tenant-id")
}

func joinFlags(flags []string) string {
	if len(flags) == 1 {
		return flags[0]
	}
	result := ""
	for i, f := range flags {
		if i > 0 && i == len(flags)-1 {
			result += " and "
		} else if i > 0 {
			result += ", "
		}
		result += f
	}
	return result
}
