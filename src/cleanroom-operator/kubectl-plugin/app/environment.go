package app

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"fmt"
	"net"
	"net/url"
	"os"
	"strings"
	"text/tabwriter"
	"time"

	"github.com/spf13/cobra"
	"golang.org/x/term"
	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
	"k8s.io/apimachinery/pkg/runtime/schema"
	"k8s.io/apimachinery/pkg/types"
	"k8s.io/apimachinery/pkg/watch"
)

var environmentGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "environments",
}

func newEnvironmentCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:     "environment",
		Aliases: []string{"env"},
		Short:   "Manage Clean Room environments",
	}

	cmd.AddCommand(newEnvironmentCreateCmd())
	cmd.AddCommand(newEnvironmentUpdateCmd())
	cmd.AddCommand(newEnvironmentGetCmd())
	cmd.AddCommand(newEnvironmentDeleteCmd())
	cmd.AddCommand(newEnvironmentListCmd())
	cmd.AddCommand(newEnvironmentStatusCmd())
	cmd.AddCommand(newEnvironmentWaitCmd())
	cmd.AddCommand(newEnvironmentReconcileCmd())
	cmd.AddCommand(newEnvironmentPreparePrereqsCmd())
	cmd.AddCommand(newEnvironmentCheckInfConCmd())
	return cmd
}

type environmentCreateOpts struct {
	file                 string
	namespace            string
	noWait               bool
	timeout              string
	infraType            string
	profiles             []string
	autoApprove          bool
	enableCA             bool
	contractId           string
	deletionPolicy       string
	enableFlexNode       bool
	nodeProvisioningMode string
	flexNodeConfig       string
	forInferencing       bool
	oidcContainerName    string
	prereqsConfig        string
	kindClusterName      string
	aksClusterName       string
}

func newEnvironmentCreateCmd() *cobra.Command {
	o := &environmentCreateOpts{}

	cmd := &cobra.Command{
		Use:   "create <name>",
		Short: "Create an Environment",
		Long: `Create an Environment custom resource. Use
--infra-type and --profile flags for a streamlined
experience, or -f to supply a full spec file.`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentCreate(
				cmd.Context(), args[0], o,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(&o.file, "file", "f", "",
		"Path to JSON file with full Environment spec "+
			"(overrides other flags)")
	f.StringVar(&o.infraType, "infra-type", "",
		"Infrastructure type (virtual, aks)")
	f.StringSliceVar(&o.profiles, "profile", nil,
		"Workload profile to enable (inferencing, "+
			"analytics, observability, monitoring); "+
			"repeatable")
	f.BoolVar(&o.autoApprove, "auto-approve", true,
		"Auto-approve governance proposals")
	f.BoolVar(&o.enableCA, "enable-ca", true,
		"Enable certificate authority")
	f.StringVar(&o.contractId, "contract-id", "",
		"Governance contract ID (defaults to env name)")
	f.StringVar(&o.deletionPolicy,
		"deletion-policy", "",
		"Deletion policy for external infrastructure "+
			"(retain, delete); defaults based on "+
			"infra-type")
	f.BoolVar(&o.enableFlexNode,
		"enable-flex-node", false,
		"Enable flex node profile")
	f.StringVar(&o.nodeProvisioningMode,
		"node-provisioning-mode", "",
		"Node provisioning mode "+
			"(manual, auto); requires "+
			"--enable-flex-node")
	f.StringVar(&o.flexNodeConfig,
		"flex-node-config", "",
		"Path to JSON file with flex node profile "+
			"configuration")
	f.BoolVar(&o.forInferencing,
		"for-inferencing", false,
		"Shortcut for --profile inferencing "+
			"--enable-flex-node "+
			"--node-provisioning-mode manual")
	f.StringVar(&o.namespace, "namespace",
		defaultNamespaceFromKubeconfig(),
		"Kubernetes namespace")
	f.BoolVar(&o.noWait, "no-wait", false,
		"Do not wait for environment to reach Ready state")
	f.StringVar(&o.timeout, "timeout", "1800s",
		"Timeout when waiting")
	f.StringVar(&o.oidcContainerName,
		"oidc-container-name", "",
		"Container name in shared OIDC storage account "+
			"(must be unique per developer/CI run)")
	f.StringVar(&o.prereqsConfig,
		"prereqs-config", "",
		"Name of the ConfigMap created by "+
			"'prepare-prereqs' containing Azure "+
			"provider configuration "+
			"(required for --infra-type aks)")
	f.StringVar(&o.kindClusterName,
		"kind-cluster-name", "",
		"Name of an existing kind cluster to target "+
			"as the workload cluster "+
			"(--infra-type virtual). When set, the "+
			"provider targets this cluster instead of "+
			"deriving a name.")
	f.StringVar(&o.aksClusterName,
		"aks-cluster-name", "",
		"Name of an existing AKS cluster to target "+
			"as the workload cluster "+
			"(--infra-type aks). When set, the "+
			"provider targets this cluster instead of "+
			"deriving a name.")
	return cmd
}

// validProfiles lists the recognized profile names.
var validProfiles = map[string]bool{
	"inferencing":   true,
	"analytics":     true,
	"observability": true,
	"monitoring":    true,
}

func contains(sl []string, s string) bool {
	for _, v := range sl {
		if v == s {
			return true
		}
	}
	return false
}

// buildProfilesSpec converts --profile flag values into
// the profiles section of the Environment spec.
func buildProfilesSpec(
	profiles []string,
) (map[string]interface{}, error) {
	p := map[string]interface{}{}
	for _, name := range profiles {
		if !validProfiles[name] {
			return nil, fmt.Errorf(
				"unknown profile %q; valid profiles: "+
					"inferencing, analytics, "+
					"observability, monitoring",
				name,
			)
		}
		switch name {
		case "inferencing":
			p["inferencing"] = map[string]interface{}{
				"kserveProfile": map[string]interface{}{
					"enabled": true,
				},
			}
		case "analytics":
			p["analytics"] = map[string]interface{}{
				"enabled": true,
			}
		case "observability":
			p["observability"] = map[string]interface{}{
				"enabled": true,
			}
		case "monitoring":
			p["monitoring"] = map[string]interface{}{
				"enabled": true,
			}
		}
	}
	return p, nil
}

func runEnvironmentCreate(
	ctx context.Context,
	name string,
	o *environmentCreateOpts,
) error {
	// Expand --for-inferencing shortcut.
	if o.forInferencing {
		if !contains(o.profiles, "inferencing") {
			o.profiles = append(
				o.profiles, "inferencing",
			)
		}
		o.enableFlexNode = true
		if o.nodeProvisioningMode == "" {
			o.nodeProvisioningMode = "manual"
		}
	}

	// Validate --node-provisioning-mode value.
	if o.nodeProvisioningMode != "" &&
		o.nodeProvisioningMode != "manual" &&
		o.nodeProvisioningMode != "auto" {
		return fmt.Errorf(
			"invalid --node-provisioning-mode %q; "+
				"valid values: manual, auto",
			o.nodeProvisioningMode,
		)
	}

	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	var spec map[string]interface{}

	if o.file != "" {
		// File-based path: read the full spec from disk.
		specData, err := os.ReadFile(o.file)
		if err != nil {
			return fmt.Errorf(
				"reading spec file: %w", err,
			)
		}
		if err := json.Unmarshal(
			specData, &spec,
		); err != nil {
			return fmt.Errorf(
				"parsing spec JSON: %w", err,
			)
		}
	} else {
		// Flag-based path: build spec from CLI flags.
		if o.infraType == "" {
			return fmt.Errorf(
				"--infra-type is required when -f " +
					"is not provided",
			)
		}

		spec = map[string]interface{}{
			"infraType":   o.infraType,
			"autoApprove": o.autoApprove,
			"enableCA":    o.enableCA,
		}

		if o.contractId != "" {
			spec["contractId"] = o.contractId
		}

		if o.deletionPolicy != "" {
			spec["deletionPolicy"] = o.deletionPolicy
		}

		if len(o.profiles) > 0 {
			profiles, err := buildProfilesSpec(
				o.profiles,
			)
			if err != nil {
				return err
			}
			spec["profiles"] = profiles
		}

		if o.enableFlexNode || o.flexNodeConfig != "" {
			profiles, _ :=
				spec["profiles"].(map[string]interface{})
			if profiles == nil {
				profiles = map[string]interface{}{}
			}

			if o.flexNodeConfig != "" {
				data, err := os.ReadFile(
					o.flexNodeConfig,
				)
				if err != nil {
					return fmt.Errorf(
						"reading flex-node-config: %w",
						err,
					)
				}
				var flexCfg map[string]interface{}
				if err := json.Unmarshal(
					data, &flexCfg,
				); err != nil {
					return fmt.Errorf(
						"parsing flex-node-config "+
							"JSON: %w", err,
					)
				}
				// Ensure enabled is set.
				flexCfg["enabled"] = true
				if o.nodeProvisioningMode != "" {
					flexCfg["mode"] =
						o.nodeProvisioningMode
				}
				profiles["flexNode"] = flexCfg
			} else {
				flexNode :=
					map[string]interface{}{
						"enabled": true,
					}
				if o.nodeProvisioningMode != "" {
					flexNode["mode"] =
						o.nodeProvisioningMode
				}
				profiles["flexNode"] = flexNode
			}
			spec["profiles"] = profiles
		}
	}

	// Validate and apply prereqs config for AKS.
	if o.prereqsConfig != "" {
		spec["prereqsConfigRef"] = o.prereqsConfig
	} else if o.infraType == "aks" && o.file == "" {
		return fmt.Errorf(
			"--prereqs-config is required for " +
				"--infra-type aks; run " +
				"'kubectl cleanroom environment " +
				"prepare-prereqs' first",
		)
	}

	// Inject cluster name overrides into
	// clusterProviderConfig so the provider targets an
	// existing workload cluster by name (idempotent).
	if o.kindClusterName != "" || o.aksClusterName != "" {
		cpc, _ := spec["clusterProviderConfig"].(map[string]interface{})
		if cpc == nil {
			cpc = map[string]interface{}{}
		}
		if o.kindClusterName != "" {
			cpc["kindClusterName"] = o.kindClusterName
		}
		if o.aksClusterName != "" {
			cpc["aksClusterName"] = o.aksClusterName
		}
		spec["clusterProviderConfig"] = cpc
	}

	// Derive oidcContainerName if not explicitly set.
	if o.oidcContainerName == "" {
		if os.Getenv("GITHUB_ACTIONS") == "true" {
			o.oidcContainerName = name + "-" +
				os.Getenv("JOB_ID") + "-" +
				os.Getenv("RUN_ID")
		} else {
			user := os.Getenv("USER")
			if os.Getenv("CODESPACES") == "true" {
				user = os.Getenv("GITHUB_USER")
			}
			if user == "" {
				return fmt.Errorf(
					"--oidc-container-name is " +
						"required or USER env var " +
						"must be set",
				)
			}
			o.oidcContainerName = name + "-" + user
		}
	}

	gsSvc, _ :=
		spec["governanceService"].(map[string]interface{})
	if gsSvc == nil {
		gsSvc = map[string]interface{}{}
	}
	gsSvc["oidcContainerName"] = o.oidcContainerName
	spec["governanceService"] = gsSvc

	obj := &unstructured.Unstructured{
		Object: map[string]interface{}{
			"apiVersion": "cleanroom.azure.com/v1alpha1",
			"kind":       "Environment",
			"metadata": map[string]interface{}{
				"name":      name,
				"namespace": o.namespace,
			},
			"spec": spec,
		},
	}

	result, err := client.Resource(environmentGVR).
		Namespace(o.namespace).
		Apply(ctx, name, obj, metav1.ApplyOptions{
			FieldManager: "kubectl-cleanroom",
		})
	if err != nil {
		return fmt.Errorf("applying Environment: %w", err)
	}

	fmt.Printf("Environment %q created in namespace %q\n",
		result.GetName(), result.GetNamespace())

	if !o.noWait {
		return runEnvironmentWait(
			ctx, name, o.namespace, "Ready", o.timeout,
		)
	}

	return nil
}

type environmentUpdateOpts struct {
	namespace            string
	noWait               bool
	timeout              string
	profiles             []string
	enableFlexNode       bool
	nodeProvisioningMode string
	flexNodeConfig       string
	forInferencing       bool
}

func newEnvironmentUpdateCmd() *cobra.Command {
	o := &environmentUpdateOpts{}

	cmd := &cobra.Command{
		Use:   "update <name>",
		Short: "Update an existing Environment",
		Long: `Update an existing Environment custom resource.
Currently supports enabling profiles and flex node on an
existing environment.`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentUpdate(
				cmd.Context(), args[0], o,
			)
		},
	}

	f := cmd.Flags()
	f.StringSliceVar(&o.profiles, "profile", nil,
		"Workload profile to enable (inferencing, "+
			"analytics, observability, monitoring); "+
			"repeatable")
	f.BoolVar(&o.enableFlexNode,
		"enable-flex-node", false,
		"Enable flex node profile")
	f.StringVar(&o.nodeProvisioningMode,
		"node-provisioning-mode", "",
		"Node provisioning mode "+
			"(manual, auto); requires "+
			"--enable-flex-node")
	f.StringVar(&o.flexNodeConfig,
		"flex-node-config", "",
		"Path to JSON file with flex node profile "+
			"configuration")
	f.BoolVar(&o.forInferencing,
		"for-inferencing", false,
		"Shortcut for --profile inferencing "+
			"--enable-flex-node "+
			"--node-provisioning-mode manual")
	f.BoolVar(&o.noWait, "no-wait", false,
		"Do not wait for environment to reach "+
			"Ready state")
	f.StringVar(&o.timeout, "timeout", "1800s",
		"Timeout when waiting")
	f.StringVar(&o.namespace, "namespace",
		defaultNamespaceFromKubeconfig(),
		"Kubernetes namespace")
	return cmd
}

func runEnvironmentUpdate(
	ctx context.Context,
	name string,
	o *environmentUpdateOpts,
) error {
	// Expand --for-inferencing shortcut.
	if o.forInferencing {
		if !contains(o.profiles, "inferencing") {
			o.profiles = append(
				o.profiles, "inferencing",
			)
		}
		o.enableFlexNode = true
		if o.nodeProvisioningMode == "" {
			o.nodeProvisioningMode = "manual"
		}
	}

	if !o.enableFlexNode &&
		o.flexNodeConfig == "" &&
		len(o.profiles) == 0 {
		return fmt.Errorf(
			"at least one of --for-inferencing, " +
				"--profile, --enable-flex-node, or " +
				"--flex-node-config must be specified")
	}

	// Validate --node-provisioning-mode value.
	if o.nodeProvisioningMode != "" &&
		o.nodeProvisioningMode != "manual" &&
		o.nodeProvisioningMode != "auto" {
		return fmt.Errorf(
			"invalid --node-provisioning-mode %q; "+
				"valid values: manual, auto",
			o.nodeProvisioningMode,
		)
	}

	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	// Build the profiles patch.
	profilesPatch := map[string]interface{}{}

	if len(o.profiles) > 0 {
		profiles, err := buildProfilesSpec(o.profiles)
		if err != nil {
			return err
		}
		for k, v := range profiles {
			profilesPatch[k] = v
		}
	}

	// Build the flex node patch.
	if o.enableFlexNode || o.flexNodeConfig != "" {
		var flexNode map[string]interface{}
		if o.flexNodeConfig != "" {
			data, err := os.ReadFile(o.flexNodeConfig)
			if err != nil {
				return fmt.Errorf(
					"reading flex-node-config: %w",
					err,
				)
			}
			if err := json.Unmarshal(
				data, &flexNode,
			); err != nil {
				return fmt.Errorf(
					"parsing flex-node-config "+
						"JSON: %w",
					err,
				)
			}
			flexNode["enabled"] = true
		} else {
			flexNode = map[string]interface{}{
				"enabled": true,
			}
		}
		if o.nodeProvisioningMode != "" {
			flexNode["mode"] =
				o.nodeProvisioningMode
		}
		profilesPatch["flexNode"] = flexNode
	}

	patch := map[string]interface{}{
		"spec": map[string]interface{}{
			"profiles": profilesPatch,
		},
	}

	patchBytes, err := json.Marshal(patch)
	if err != nil {
		return fmt.Errorf("marshaling patch: %w", err)
	}

	result, err := client.Resource(environmentGVR).
		Namespace(o.namespace).
		Patch(
			ctx, name,
			types.MergePatchType,
			patchBytes,
			metav1.PatchOptions{},
		)
	if err != nil {
		return fmt.Errorf(
			"patching Environment: %w", err,
		)
	}

	fmt.Printf(
		"Environment %q updated in namespace %q\n",
		result.GetName(), result.GetNamespace(),
	)

	if !o.noWait {
		return runEnvironmentWait(
			ctx, name, o.namespace, "Ready", o.timeout,
		)
	}

	return nil
}

func newEnvironmentGetCmd() *cobra.Command {
	var namespace string
	var output string

	cmd := &cobra.Command{
		Use:   "get <name>",
		Short: "Get an Environment",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentGet(
				cmd.Context(), args[0], namespace, output,
			)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	cmd.Flags().StringVarP(&output, "output", "o",
		"", "Output format (json, yaml)")
	return cmd
}

func runEnvironmentGet(
	ctx context.Context,
	name, namespace, output string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	result, err := client.Resource(environmentGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Environment: %w", err)
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

func newEnvironmentDeleteCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "delete <name>",
		Short: "Delete an Environment",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentDelete(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runEnvironmentDelete(
	ctx context.Context,
	name, namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if err := client.Resource(environmentGVR).
		Namespace(namespace).
		Delete(ctx, name, metav1.DeleteOptions{}); err != nil {
		return fmt.Errorf("deleting Environment: %w", err)
	}

	fmt.Printf("Environment %q deleted\n", name)
	return nil
}

func newEnvironmentListCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "list",
		Short: "List Environments",
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentList(cmd.Context(), namespace)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runEnvironmentList(
	ctx context.Context, namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	list, err := client.Resource(environmentGVR).
		Namespace(namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return fmt.Errorf("listing Environments: %w", err)
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

func newEnvironmentStatusCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "status <name>",
		Short: "Show detailed status of an Environment and its children",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentStatus(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	return cmd
}

func runEnvironmentStatus(
	ctx context.Context,
	name, namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	env, err := client.Resource(environmentGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Environment: %w", err)
	}

	phase, _, _ := unstructured.NestedString(
		env.Object, "status", "phase",
	)
	infraType, _, _ := unstructured.NestedString(
		env.Object, "spec", "infraType",
	)

	fmt.Printf("Environment: %s\n", name)
	fmt.Printf("  Phase:     %s\n", phase)
	if phase == "Failed" {
		msg, _, _ := unstructured.NestedString(
			env.Object, "status", "message",
		)
		if msg != "" {
			fmt.Printf("  Message:   %s\n", msg)
		}
	}
	traceId, _, _ := unstructured.NestedString(
		env.Object, "status", "lastOperationTraceId",
	)
	if traceId != "" {
		fmt.Printf("  TraceId:   %s\n", traceId)
	}
	fmt.Printf("  InfraType: %s\n", infraType)

	// Print profiles summary.
	profiles, _, _ := unstructured.NestedMap(
		env.Object, "spec", "profiles",
	)
	if len(profiles) > 0 {
		fmt.Println("  Profiles:")
		inferencing, _, _ :=
			unstructured.NestedMap(
				env.Object, "spec", "profiles",
				"inferencing",
			)
		if inferencing != nil {
			enabled, _, _ :=
				unstructured.NestedBool(
					env.Object, "spec", "profiles",
					"inferencing", "kserveProfile",
					"enabled",
				)
			if enabled {
				fmt.Println(
					"    Inferencing: enabled",
				)
			}
		}
		flexNode, _, _ := unstructured.NestedMap(
			env.Object, "spec", "profiles",
			"flexNode",
		)
		if flexNode != nil {
			flexEnabled, _, _ :=
				unstructured.NestedBool(
					env.Object, "spec", "profiles",
					"flexNode", "enabled",
				)
			if flexEnabled {
				mode, _, _ :=
					unstructured.NestedString(
						env.Object, "spec",
						"profiles", "flexNode",
						"mode",
					)
				if mode == "" {
					mode = "manual"
				}
				fmt.Printf(
					"    FlexNode:    enabled "+
						"(mode: %s)\n",
					mode,
				)
			}
		}
		analytics, _, _ := unstructured.NestedBool(
			env.Object, "spec", "profiles",
			"analytics", "enabled",
		)
		if analytics {
			fmt.Println(
				"    Analytics:   enabled",
			)
		}
		observability, _, _ :=
			unstructured.NestedBool(
				env.Object, "spec", "profiles",
				"observability", "enabled",
			)
		if observability {
			fmt.Println(
				"    Observability: enabled",
			)
		}
		monitoring, _, _ :=
			unstructured.NestedBool(
				env.Object, "spec", "profiles",
				"monitoring", "enabled",
			)
		if monitoring {
			fmt.Println(
				"    Monitoring:  enabled",
			)
		}
	} else {
		fmt.Println("  Profiles:  none")
	}
	fmt.Println()

	// Print conditions.
	conditions, _, _ := unstructured.NestedSlice(
		env.Object, "status", "conditions",
	)
	if len(conditions) > 0 {
		fmt.Println("Conditions:")
		w := tabwriter.NewWriter(
			os.Stdout, 0, 4, 2, ' ', 0,
		)
		fmt.Fprintf(w, "  \tTYPE\tSTATUS\tREASON\tMESSAGE\n")
		for _, c := range conditions {
			cMap, ok := c.(map[string]interface{})
			if !ok {
				continue
			}
			cType, _ := cMap["type"].(string)
			cStatus, _ := cMap["status"].(string)
			cReason, _ := cMap["reason"].(string)
			cMsg, _ := cMap["message"].(string)
			indicator := "?"
			if cStatus == "True" {
				indicator = "✓"
			} else if cStatus == "False" {
				if cReason == "Failed" ||
					cReason == "ChildFailed" {
					indicator = "✗"
				} else {
					indicator = "○"
				}
			}
			fmt.Fprintf(w, "  %s\t%s\t%s\t%s\t%s\n",
				indicator, cType, cStatus, cReason, cMsg)
		}
		w.Flush()
		fmt.Println()
	}

	// Show child resource statuses.
	fmt.Println("Children:")
	w := tabwriter.NewWriter(os.Stdout, 0, 4, 2, ' ', 0)
	fmt.Fprintf(w, "  KIND\tNAME\tPHASE\n")

	childResources := []struct {
		gvr  schema.GroupVersionResource
		kind string
		name string
	}{
		{
			gvr: schema.GroupVersionResource{
				Group:    "cleanroom.azure.com",
				Version:  "v1alpha1",
				Resource: "ccfmembers",
			},
			kind: "CcfMember",
			name: name + "-member",
		},
		{
			gvr:  ccfNetworkGVR,
			kind: "CcfNetwork",
			name: name + "-network",
		},
		{
			gvr: schema.GroupVersionResource{
				Group:    "cleanroom.azure.com",
				Version:  "v1alpha1",
				Resource: "governancecontracts",
			},
			kind: "GovernanceContract",
			name: name + "-contract",
		},
		{
			gvr:  clusterGVR,
			kind: "Cluster",
			name: name + "-cluster",
		},
	}

	for _, child := range childResources {
		cr, err := client.Resource(child.gvr).
			Namespace(namespace).
			Get(ctx, child.name, metav1.GetOptions{})
		if err != nil {
			fmt.Fprintf(w, "  %s\t%s\t%s\n",
				child.kind, child.name, "<not found>")
			continue
		}
		childPhase, _, _ := unstructured.NestedString(
			cr.Object, "status", "phase",
		)
		fmt.Fprintf(w, "  %s\t%s\t%s\n",
			child.kind, child.name, childPhase)
	}
	w.Flush()
	return nil
}

func newEnvironmentWaitCmd() *cobra.Command {
	var namespace string
	var forPhase string
	var timeout string

	cmd := &cobra.Command{
		Use:   "wait <name>",
		Short: "Wait for an Environment to reach a phase",
		Args:  cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentWait(
				cmd.Context(), args[0],
				namespace, forPhase, timeout,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(&namespace, "namespace",
		defaultNamespaceFromKubeconfig(), "Kubernetes namespace")
	f.StringVar(&forPhase, "for",
		"Ready", "Phase to wait for")
	f.StringVar(&timeout, "timeout",
		"1800s", "Timeout duration")
	return cmd
}

func runEnvironmentWait(
	ctx context.Context,
	name, namespace, forPhase, timeout string,
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

	fmt.Printf("Waiting for Environment %q to reach %s...\n",
		name, forPhase)

	start := time.Now()

	// Fetch the Environment's creation timestamp to use
	// as the event cutoff — events before the resource
	// was created belong to a prior same-named instance.
	env, err := dynClient.Resource(environmentGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Environment: %w", err)
	}
	envCreated := env.GetCreationTimestamp().Time

	watcher, err := dynClient.Resource(environmentGVR).
		Namespace(namespace).
		Watch(ctx, metav1.ListOptions{
			FieldSelector: fmt.Sprintf(
				"metadata.name=%s", name,
			),
		})
	if err != nil {
		return fmt.Errorf("watching Environment: %w", err)
	}
	defer watcher.Stop()

	// Watch Events from child resources.
	eventsCh := make(chan watch.Event)
	childEventWatcher, err := startChildEventWatcher(
		ctx, namespace, name, envCreated, eventsCh,
	)
	if err == nil {
		defer childEventWatcher.Stop()
	}

	useColor := term.IsTerminal(int(os.Stdout.Fd()))

	// Track conditions we have already printed to avoid
	// duplicating output on every requeue.
	// Value is "status:icon" so we re-print when the icon
	// changes (e.g. ○ → ✗ on phase=Failed).
	printedConditions := make(map[string]string)
	// Track which conditions have reached a terminal
	// state — suppress events for completed resources.
	doneConditions := make(map[string]bool)
	// Track child events already printed (kind:message).
	printedEvents := make(map[string]bool)
	lastPhase := ""

	for {
		select {
		case <-ctx.Done():
			return fmt.Errorf(
				"timed out waiting for Environment %q "+
					"to reach %s after %s",
				name, forPhase,
				time.Since(start).Round(time.Second),
			)
		case ce, ok := <-eventsCh:
			if !ok {
				eventsCh = nil
				continue
			}
			ev, ok := ce.Object.(*corev1.Event)
			if !ok {
				continue
			}
			kind := ev.InvolvedObject.Kind
			objName := ev.InvolvedObject.Name
			// Skip events that predate the resource's
			// creation — they belong to a prior
			// same-named instance.
			evTime := ev.LastTimestamp.Time
			if evTime.IsZero() {
				evTime = ev.CreationTimestamp.Time
			}
			if !evTime.IsZero() && evTime.Before(envCreated) {
				continue
			}
			// Suppress events for completed resources.
			// Use object-name-aware condition lookup
			// for CcfMember which has two instances.
			cond := conditionForObject(
				kind, objName, name,
			)
			if cond != "" && doneConditions[cond] {
				continue
			}
			msg := ev.Message
			// Include object name in dedup key so
			// member0 and operator don't suppress
			// each other's identical messages.
			key := kind + ":" + objName + ":" + msg
			if printedEvents[key] {
				continue
			}
			printedEvents[key] = true
			elapsed := time.Since(start).
				Round(time.Second)
			label := childLabel(
				kind, objName, name,
			)
			line := fmt.Sprintf(
				"  %-8s · %s: %s",
				elapsed, label, msg,
			)
			if useColor {
				c := kindColor(kind)
				fmt.Printf("%s%s%s\n",
					c, line, colorReset)
			} else {
				fmt.Println(line)
			}
		case event, ok := <-watcher.ResultChan():
			if !ok {
				return fmt.Errorf("watch channel closed")
			}
			obj, ok := event.Object.(*unstructured.Unstructured)
			if !ok {
				continue
			}

			phase, _, _ := unstructured.NestedString(
				obj.Object, "status", "phase",
			)

			// Print phase transitions.
			if phase != "" && phase != lastPhase {
				elapsed := time.Since(start).
					Round(time.Second)
				if phase == "Provisioning" {
					fmt.Printf("  %-8s %s\n",
						elapsed, "Provisioning started")
				} else if phase != "Ready" &&
					phase != "Failed" {
					fmt.Printf("  %-8s Phase: %s\n",
						elapsed, phase)
				}
				lastPhase = phase
			}

			// Print condition updates.
			conditions, _, _ := unstructured.NestedSlice(
				obj.Object, "status", "conditions",
			)
			for _, c := range conditions {
				cMap, ok := c.(map[string]interface{})
				if !ok {
					continue
				}
				cType, _ := cMap["type"].(string)
				cStatus, _ := cMap["status"].(string)
				cMsg, _ := cMap["message"].(string)

				// Skip the top-level EnvironmentReady
				// condition since it just mirrors phase.
				if cType == "EnvironmentReady" {
					continue
				}

				elapsed := time.Since(start).
					Round(time.Second)

				if cStatus == "True" {
					key := cType + ":✓"
					if printedConditions[cType] == key {
						continue
					}
					printedConditions[cType] = key
					doneConditions[cType] = true
					waitingOn := remainingConditions(
						doneConditions,
					)
					if len(waitingOn) > 0 {
						fmt.Printf(
							"  %-8s ✓ %s"+
								" (waiting: %s)\n",
							elapsed, cType,
							strings.Join(
								waitingOn, ", ",
							),
						)
					} else {
						fmt.Printf(
							"  %-8s ✓ %s\n",
							elapsed, cType,
						)
					}
				} else if cStatus == "False" {
					// Truncate long messages for
					// inline display.
					msg := cMsg
					if len(msg) > 120 {
						msg = msg[:117] + "..."
					}
					// Use ○ for in-progress and ✗
					// for terminal failures.
					cReason, _ := cMap["reason"].(string)
					icon := "○"
					if cReason == "Failed" ||
						cReason == "ChildFailed" {
						icon = "✗"
					}
					key := cType + ":" + icon
					if printedConditions[cType] == key {
						continue
					}
					printedConditions[cType] = key
					fmt.Printf("  %-8s %s %s - %s\n",
						elapsed, icon, cType, msg)
				}
			}

			if phase == forPhase {
				elapsed := time.Since(start).
					Round(time.Second)
				traceId, _, _ := unstructured.
					NestedString(
						obj.Object,
						"status",
						"lastOperationTraceId",
					)
				if traceId != "" {
					fmt.Printf(
						"Environment %q reached"+
							" %s (%s, trace:"+
							" %s)\n",
						name, forPhase,
						elapsed, traceId,
					)
				} else {
					fmt.Printf(
						"Environment %q reached"+
							" %s (%s)\n",
						name, forPhase, elapsed,
					)
				}
				return nil
			}
			if phase == "Failed" && forPhase != "Failed" {
				msg, _, _ := unstructured.NestedString(
					obj.Object, "status", "message",
				)
				traceId, _, _ := unstructured.
					NestedString(
						obj.Object,
						"status",
						"lastOperationTraceId",
					)
				traceInfo := ""
				if traceId != "" {
					traceInfo = fmt.Sprintf(
						" (trace: %s)", traceId,
					)
				}
				if msg != "" {
					return fmt.Errorf(
						"Environment %q failed: %s%s",
						name, msg, traceInfo,
					)
				}
				return fmt.Errorf(
					"Environment %q entered Failed "+
						"state%s",
					name, traceInfo,
				)
			}
		}
	}
}

// ANSI color codes.
const (
	colorReset   = "\033[0m"
	colorRed     = "\033[31m"
	colorCyan    = "\033[36m"
	colorYellow  = "\033[33m"
	colorMagenta = "\033[35m"
	colorBlue    = "\033[34m"
	colorGreen   = "\033[32m"
	colorOrange  = "\033[38;5;208m"
)

// kindToCondition maps an involvedObject Kind to its
// corresponding Environment condition name.
var kindToCondition = map[string]string{
	"CcfNetwork":         "CcfNetworkReady",
	"CcfMember":          "CcfMemberReady",
	"GovernanceService":  "GovernanceServiceReady",
	"GovernanceContract": "GovernanceContractReady",
	"WorkloadGovernance": "WorkloadGovernanceReady",
	"Cluster":            "ClusterReady",
}

// allConditions is the ordered list of conditions to track
// for the "waiting on" display.
var allConditions = []string{
	"CcfNetworkReady",
	"CcfMemberReady",
	"InitialMemberReady",
	"GovernanceServiceReady",
	"WorkloadGovernanceReady",
	"ClusterRunning",
	"ClusterReady",
}

// conditionForObject returns the Environment condition name
// for a given child event. For CcfMember it disambiguates
// based on object name suffix.
func conditionForObject(
	kind, objName, envName string,
) string {
	if kind == "CcfMember" {
		if objName == envName+"-member0" {
			return "InitialMemberReady"
		}
		return "CcfMemberReady"
	}
	return kindToCondition[kind]
}

// childLabel returns a display label for a child event.
// For CcfMember it adds a parenthetical to distinguish
// the two instances.
func childLabel(
	kind, objName, envName string,
) string {
	if kind == "CcfMember" {
		if objName == envName+"-member0" {
			return "CcfMember (member0)"
		}
		return "CcfMember (operator)"
	}
	return kind
}

// remainingConditions returns the condition names that have
// not yet reached True, in a stable order.
func remainingConditions(
	done map[string]bool,
) []string {
	var remaining []string
	for _, c := range allConditions {
		if !done[c] {
			remaining = append(remaining, c)
		}
	}
	return remaining
}

// kindColor returns the ANSI color code for a given Kind.
func kindColor(kind string) string {
	switch kind {
	case "CcfNetwork":
		return colorCyan
	case "CcfMember":
		return colorYellow
	case "GovernanceService":
		return colorMagenta
	case "GovernanceContract":
		return colorBlue
	case "WorkloadGovernance":
		return colorGreen
	case "Cluster":
		return colorOrange
	default:
		return ""
	}
}

// startChildEventWatcher watches Kubernetes Events in the
// namespace and forwards those whose involvedObject matches
// a known child resource name to the provided channel. The
// returned watcher must be stopped by the caller.
func startChildEventWatcher(
	ctx context.Context,
	namespace, envName string,
	notBefore time.Time,
	ch chan<- watch.Event,
) (watch.Interface, error) {
	clientset, err := getClientset()
	if err != nil {
		return nil, err
	}

	// Known child resource name prefixes.
	childPrefixes := []string{
		envName + "-network",
		envName + "-member",
		envName + "-operator",
		envName + "-gs",
		envName + "-contract",
		envName + "-cluster",
		envName + "-wg-",
	}

	isChildEvent := func(name string) bool {
		for _, p := range childPrefixes {
			if name == p ||
				strings.HasPrefix(name, p) {
				return true
			}
		}
		return false
	}

	// List existing events so we can replay recent
	// OperationProgress events that were emitted before
	// the watch started, then watch for new events.
	eventList, err := clientset.CoreV1().
		Events(namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return nil, err
	}

	w, err := clientset.CoreV1().Events(namespace).
		Watch(ctx, metav1.ListOptions{
			ResourceVersion: eventList.ResourceVersion,
		})
	if err != nil {
		return nil, err
	}

	go func() {
		defer close(ch)
		// Replay recent OperationProgress events from
		// the listing so the wait output shows progress
		// that occurred before the watch started.
		// Use notBefore (the wait start time) as the
		// cutoff so events from prior runs are excluded.
		for i := range eventList.Items {
			ev := &eventList.Items[i]
			if !isChildEvent(ev.InvolvedObject.Name) {
				continue
			}
			if ev.Reason != "OperationProgress" {
				continue
			}
			evTime := ev.LastTimestamp.Time
			if evTime.IsZero() {
				evTime = ev.CreationTimestamp.Time
			}
			if evTime.Before(notBefore) {
				continue
			}
			ch <- watch.Event{
				Type:   watch.Added,
				Object: ev,
			}
		}
		for ev := range w.ResultChan() {
			event, ok := ev.Object.(*corev1.Event)
			if !ok {
				continue
			}
			if isChildEvent(
				event.InvolvedObject.Name,
			) {
				ch <- ev
			}
		}
	}()

	return w, nil
}

func newEnvironmentReconcileCmd() *cobra.Command {
	var namespace string
	var noWait bool
	var timeout string

	cmd := &cobra.Command{
		Use:     "reconcile <name>",
		Aliases: []string{"retry"},
		Short:   "Trigger reconciliation of an Environment",
		Long: `Force the operator to re-evaluate the
Environment and all its children. Works on any phase —
not just Failed. Sets the
reconcile.cleanroom.azure.com/requestedAt annotation
and waits for the controller to acknowledge it.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runEnvironmentReconcile(
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
		"Do not wait for environment to reach Ready "+
			"state after triggering reconciliation")
	f.StringVar(&timeout, "timeout", "1800s",
		"Timeout when waiting")
	return cmd
}

func runEnvironmentReconcile(
	ctx context.Context,
	name, namespace string,
	wait bool,
	timeout string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	env, err := client.Resource(environmentGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Environment: %w", err)
	}

	phase, _, _ := unstructured.NestedString(
		env.Object, "status", "phase",
	)

	// Set reconcile timestamp annotation.
	requestedAt := time.Now().UTC().Format(
		time.RFC3339Nano,
	)
	annotations := env.GetAnnotations()
	if annotations == nil {
		annotations = map[string]string{}
	}
	annotations["reconcile.cleanroom.azure.com/requestedAt"] = requestedAt
	env.SetAnnotations(annotations)

	_, err = client.Resource(environmentGVR).
		Namespace(namespace).
		Update(ctx, env, metav1.UpdateOptions{})
	if err != nil {
		return fmt.Errorf(
			"setting reconcile annotation: %w", err,
		)
	}

	fmt.Printf(
		"► reconciliation triggered for "+
			"Environment %q (was: %s)\n",
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
					environmentGVR,
				).Namespace(namespace).Get(
					waitCtx, name, metav1.GetOptions{},
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
					return runEnvironmentWait(
						ctx, name, namespace,
						"Ready", timeout,
					)
				}
			}
		}
	}

	return nil
}

func newEnvironmentCheckInfConCmd() *cobra.Command {
	var namespace string
	var timeout time.Duration

	cmd := &cobra.Command{
		Use:   "check-inferencing-connectivity <name>",
		Short: "Check network connectivity to the inferencing endpoint",
		Long: `Verifies that the inferencing endpoint associated with
this environment is reachable from this machine by performing
DNS resolution, TCP connect, and TLS handshake checks.
If the TLS handshake fails, suggests connecting via VPN.`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runEnvironmentCheckEndpoint(
				cmd.Context(), args[0], namespace, timeout,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(
		&namespace,
		"namespace",
		defaultNamespaceFromKubeconfig(),
		"Kubernetes namespace",
	)
	f.DurationVar(
		&timeout,
		"timeout",
		10*time.Second,
		"connection timeout",
	)
	return cmd
}

func runEnvironmentCheckEndpoint(
	ctx context.Context,
	envName, namespace string,
	timeout time.Duration,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	// Verify the environment has the inferencing profile.
	env, err := client.Resource(environmentGVR).
		Namespace(namespace).
		Get(ctx, envName, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf("getting Environment: %w", err)
	}

	infraType, _, _ := unstructured.NestedString(
		env.Object, "spec", "infraType",
	)
	if infraType == "virtual" {
		fmt.Println(
			"Connectivity check is not supported" +
				" for virtual environments." +
				" The inferencing endpoint uses an" +
				" in-cluster .svc address that is" +
				" only reachable from within the" +
				" workload cluster. Use kubectl" +
				" port-forward to reach the" +
				" endpoint from your machine.",
		)
		return nil
	}

	enabled, _, _ := unstructured.NestedBool(
		env.Object, "spec", "profiles",
		"inferencing", "kserveProfile", "enabled",
	)
	if !enabled {
		return fmt.Errorf(
			"environment %q does not have the"+
				" inferencing kserve profile enabled",
			envName,
		)
	}

	// Get the endpoint from the Cluster status.
	clusterName := envName + "-cluster"
	cluster, err := client.Resource(clusterGVR).
		Namespace(namespace).
		Get(ctx, clusterName, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting Cluster %s: %w", clusterName, err,
		)
	}

	endpoint, _, _ := unstructured.NestedString(
		cluster.Object, "status",
		"inferencingWorkloadProfile",
		"kserveProfile", "endpoint",
	)
	if endpoint == "" {
		return fmt.Errorf(
			"cluster %s does not have an inferencing"+
				" endpoint yet; wait for the cluster"+
				" to be ready",
			clusterName,
		)
	}

	// Get the CA cert from the GovernanceContract.
	gcName := envName + "-wg-inferencing-gc"
	gc, err := client.Resource(governanceContractGVR).
		Namespace(namespace).
		Get(ctx, gcName, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting GovernanceContract %s: %w",
			gcName, err,
		)
	}

	caCert, _, _ := unstructured.NestedString(
		gc.Object, "status", "caCert",
	)
	if caCert == "" {
		return fmt.Errorf(
			"governance contract %s does not have a"+
				" CA certificate yet",
			gcName,
		)
	}

	u, err := url.Parse(endpoint)
	if err != nil {
		return fmt.Errorf("parsing endpoint URL: %w", err)
	}

	host := u.Hostname()
	port := u.Port()
	if port == "" {
		port = "443"
	}
	hostPort := net.JoinHostPort(host, port)

	fmt.Printf("Checking endpoint: %s\n\n", endpoint)

	// 1. DNS resolution.
	addrs, err := net.DefaultResolver.LookupHost(ctx, host)
	if err != nil {
		fmt.Printf(
			"✗ dns — cannot resolve %s: %v\n", host, err,
		)
		return fmt.Errorf("DNS resolution failed")
	}
	fmt.Printf("✓ dns — %s resolves to %s\n", host, addrs[0])

	// 2. TCP connect.
	dialer := &net.Dialer{Timeout: timeout}
	tcpConn, err := dialer.DialContext(ctx, "tcp", hostPort)
	if err != nil {
		fmt.Printf(
			"✗ tcp — cannot connect to %s: %v\n",
			hostPort, err,
		)
		return fmt.Errorf("TCP connect failed")
	}
	tcpConn.Close()
	fmt.Printf("✓ tcp — connected to %s\n", hostPort)

	// 3. TLS handshake with the Clean Room CA.
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM([]byte(caCert)) {
		return fmt.Errorf(
			"invalid CA certificate in GovernanceContract"+
				" %s",
			gcName,
		)
	}

	tlsConn, err := tls.DialWithDialer(
		dialer, "tcp", hostPort,
		&tls.Config{
			RootCAs:    pool,
			ServerName: host,
		},
	)
	if err != nil {
		if ne, ok := err.(net.Error); ok && ne.Timeout() {
			fmt.Printf(
				"✗ tls — handshake timed out after %s\n",
				timeout,
			)
		} else {
			fmt.Printf(
				"✗ tls — handshake failed: %v\n", err,
			)
		}
		fmt.Println()
		fmt.Println(
			"The endpoint is not reachable from this" +
				" network. This is typically caused by" +
				" a corporate firewall blocking" +
				" outbound TLS to endpoints with" +
				" non-standard CAs.",
		)
		fmt.Println()
		fmt.Println(
			"Suggestion: connect via VPN and retry.",
		)
		return fmt.Errorf("TLS handshake failed")
	}
	tlsConn.Close()
	fmt.Printf("✓ tls — handshake succeeded\n")

	fmt.Println()
	fmt.Println("Endpoint is reachable.")
	return nil
}
