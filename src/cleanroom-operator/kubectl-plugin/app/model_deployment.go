package app

import (
	"context"
	"encoding/json"
	"fmt"
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
	"k8s.io/apimachinery/pkg/watch"
)

var modelDeploymentGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "modeldeployments",
}

func newModelDeploymentCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:     "model-deployment",
		Aliases: []string{"md"},
		Short:   "Manage model deployment instances",
	}

	cmd.AddCommand(newModelDeploymentCreateCmd())
	cmd.AddCommand(newModelDeploymentGetCmd())
	cmd.AddCommand(newModelDeploymentListCmd())
	cmd.AddCommand(newModelDeploymentDeleteCmd())
	cmd.AddCommand(newModelDeploymentStatusCmd())
	cmd.AddCommand(newModelDeploymentWaitCmd())
	cmd.AddCommand(newModelDeploymentReconcileCmd())
	return cmd
}

type modelDeploymentCreateOpts struct {
	namespace            string
	modelRegistration      string
	nodeProvisioningMode string
	noWait               bool
	timeout              string

	// Predictor overrides.
	predictorSpecFile string
	minReplicas       int32
	maxReplicas       int32
	inferenceTimeout  int32
	modelFormat       string
	runtime           string
	resourceRequests  []string
	resourceLimits    []string
	args              []string
	env               []string
}

func newModelDeploymentCreateCmd() *cobra.Command {
	o := &modelDeploymentCreateOpts{}

	cmd := &cobra.Command{
		Use:   "create <name>",
		Short: "Create a model deployment instance",
		Long: `Create a ModelDeployment custom
resource. This deploys the model registered by
the referenced ModelRegistration to a KServe
inferencing endpoint.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelDeploymentCreate(
				cmd.Context(), args[0], o,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(&o.namespace, "namespace", "n",
		"", "Kubernetes namespace "+
			"(defaults to current context)")
	f.StringVar(&o.modelRegistration,
		"model-registration", "",
		"Name of the ModelRegistration resource "+
			"(required)")
	f.StringVar(&o.nodeProvisioningMode,
		"node-provisioning-mode", "",
		"Node provisioning mode (auto, manual)")
	f.BoolVar(&o.noWait, "no-wait", false,
		"Do not wait for the instance to be ready")
	f.StringVar(&o.timeout, "timeout", "10m",
		"Timeout for waiting")

	// Predictor overrides.
	f.StringVar(&o.predictorSpecFile,
		"predictor-spec", "",
		"Path to a JSON file containing the "+
			"predictor spec (mutually exclusive "+
			"with individual predictor flags)")
	f.Int32Var(&o.minReplicas,
		"min-replicas", 0,
		"Minimum number of replicas")
	f.Int32Var(&o.maxReplicas,
		"max-replicas", 0,
		"Maximum number of replicas")
	f.Int32Var(&o.inferenceTimeout,
		"inference-timeout", 0,
		"Inference timeout in seconds")
	f.StringVar(&o.modelFormat,
		"model-format", "",
		"Model format (e.g. gguf, sklearn)")
	f.StringVar(&o.runtime,
		"runtime", "",
		"Serving runtime "+
			"(e.g. llamacpp-server, vllm-openai)")
	f.StringArrayVar(&o.resourceRequests,
		"resource-request", nil,
		"Resource request in KEY=VALUE format "+
			"(e.g. cpu=1, memory=2Gi; repeatable)")
	f.StringArrayVar(&o.resourceLimits,
		"resource-limit", nil,
		"Resource limit in KEY=VALUE format "+
			"(e.g. cpu=2, memory=4Gi; repeatable)")
	f.StringArrayVar(&o.args,
		"arg", nil,
		"Additional serving container argument "+
			"(can be repeated)")
	f.StringArrayVar(&o.env,
		"env", nil,
		"Environment variable in KEY=VALUE format "+
			"(can be repeated)")

	_ = cmd.MarkFlagRequired("model-registration")

	return cmd
}

func runModelDeploymentCreate(
	ctx context.Context,
	name string,
	o *modelDeploymentCreateOpts,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if o.namespace == "" {
		o.namespace = defaultNamespaceFromKubeconfig()
	}

	// Validate --node-provisioning-mode value.
	if o.nodeProvisioningMode != "" &&
		o.nodeProvisioningMode != "auto" &&
		o.nodeProvisioningMode != "manual" {
		return fmt.Errorf(
			"invalid --node-provisioning-mode %q; "+
				"valid values: auto, manual",
			o.nodeProvisioningMode,
		)
	}

	predictor, err := resolvePredictorSpec(o)
	if err != nil {
		return err
	}

	spec := map[string]interface{}{
		"modelRegistrationRef": o.modelRegistration,
	}

	if o.nodeProvisioningMode != "" {
		spec["nodeProvisioningMode"] =
			o.nodeProvisioningMode
	}

	if predictor != nil {
		spec["predictor"] = predictor
	}

	obj := &unstructured.Unstructured{
		Object: map[string]interface{}{
			"apiVersion": "cleanroom.azure.com/" +
				"v1alpha1",
			"kind": "ModelDeployment",
			"metadata": map[string]interface{}{
				"name":      name,
				"namespace": o.namespace,
			},
			"spec": spec,
		},
	}

	result, err := client.Resource(modelDeploymentGVR).
		Namespace(o.namespace).
		Apply(ctx, name, obj, metav1.ApplyOptions{
			FieldManager: "kubectl-cleanroom",
		})
	if err != nil {
		return fmt.Errorf(
			"applying ModelDeployment: %w",
			err,
		)
	}

	fmt.Printf(
		"ModelDeployment %q created in "+
			"namespace %q\n",
		result.GetName(), result.GetNamespace(),
	)

	if !o.noWait {
		return runModelDeploymentWait(
			ctx, name, o.namespace, "Ready",
			o.timeout,
		)
	}

	return nil
}

// resolvePredictorSpec returns the predictor map from
// either --predictor-spec file or individual flags.
// Returns an error if both are provided.
func resolvePredictorSpec(
	o *modelDeploymentCreateOpts,
) (map[string]interface{}, error) {
	fromFlags := buildPredictorSpec(o)

	if o.predictorSpecFile == "" {
		return fromFlags, nil
	}

	if fromFlags != nil {
		return nil, fmt.Errorf(
			"--predictor-spec cannot be combined " +
				"with individual predictor flags " +
				"(--min-replicas, --max-replicas, " +
				"--inference-timeout, " +
				"--model-format, --runtime, " +
				"--resource-request, " +
				"--resource-limit, --arg, --env)")
	}

	data, err := os.ReadFile(o.predictorSpecFile)
	if err != nil {
		return nil, fmt.Errorf(
			"reading predictor spec file %q: %w",
			o.predictorSpecFile, err,
		)
	}

	var predictor map[string]interface{}
	if err := json.Unmarshal(data, &predictor); err != nil {
		return nil, fmt.Errorf(
			"parsing predictor spec JSON from %q: %w",
			o.predictorSpecFile, err,
		)
	}

	return predictor, nil
}

// buildPredictorSpec constructs the predictor map from
// CLI flags. Returns nil if no predictor flags are set.
func buildPredictorSpec(
	o *modelDeploymentCreateOpts,
) map[string]interface{} {
	hasPredictorFlags := o.minReplicas > 0 ||
		o.maxReplicas > 0 ||
		o.inferenceTimeout > 0 ||
		o.modelFormat != "" ||
		o.runtime != "" ||
		len(o.resourceRequests) > 0 ||
		len(o.resourceLimits) > 0 ||
		len(o.args) > 0 ||
		len(o.env) > 0
	if !hasPredictorFlags {
		return nil
	}

	predictor := map[string]interface{}{}

	if o.minReplicas > 0 {
		predictor["minReplicas"] = int64(o.minReplicas)
	}
	if o.maxReplicas > 0 {
		predictor["maxReplicas"] = int64(o.maxReplicas)
	}
	if o.inferenceTimeout > 0 {
		predictor["timeout"] = int64(o.inferenceTimeout)
	}

	model := map[string]interface{}{}
	if o.modelFormat != "" {
		model["modelFormat"] = map[string]interface{}{
			"name": o.modelFormat,
		}
	}
	if o.runtime != "" {
		model["runtime"] = o.runtime
	}
	if len(o.resourceRequests) > 0 ||
		len(o.resourceLimits) > 0 {
		resources := map[string]interface{}{}
		if len(o.resourceRequests) > 0 {
			resources["requests"] =
				parseKeyValuePairs(o.resourceRequests)
		}
		if len(o.resourceLimits) > 0 {
			resources["limits"] =
				parseKeyValuePairs(o.resourceLimits)
		}
		model["resources"] = resources
	}
	if len(o.args) > 0 {
		model["args"] = o.args
	}
	if len(o.env) > 0 {
		envList := make(
			[]map[string]interface{}, 0, len(o.env),
		)
		for _, kv := range o.env {
			parts := strings.SplitN(kv, "=", 2)
			entry := map[string]interface{}{
				"name": parts[0],
			}
			if len(parts) == 2 {
				entry["value"] = parts[1]
			}
			envList = append(envList, entry)
		}
		model["env"] = envList
	}

	if len(model) > 0 {
		predictor["model"] = model
	}

	return predictor
}

// parseKeyValuePairs converts a slice of "KEY=VALUE"
// strings into a map.
func parseKeyValuePairs(
	pairs []string,
) map[string]interface{} {
	result := make(
		map[string]interface{}, len(pairs),
	)
	for _, kv := range pairs {
		parts := strings.SplitN(kv, "=", 2)
		if len(parts) == 2 {
			result[parts[0]] = parts[1]
		}
	}
	return result
}

func newModelDeploymentGetCmd() *cobra.Command {
	var namespace string
	var outputFormat string

	cmd := &cobra.Command{
		Use:   "get <name>",
		Short: "Get a ModelDeployment resource",
		Args:  cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelDeploymentGet(
				cmd.Context(), args[0],
				namespace, outputFormat,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(&namespace, "namespace", "n", "",
		"Kubernetes namespace")
	f.StringVarP(&outputFormat, "output", "o", "",
		"Output format: json or yaml")
	return cmd
}

func runModelDeploymentGet(
	ctx context.Context,
	name string,
	namespace string,
	outputFormat string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if namespace == "" {
		namespace = defaultNamespaceFromKubeconfig()
	}

	result, err := client.Resource(modelDeploymentGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting ModelDeployment: %w",
			err,
		)
	}

	if outputFormat == "json" {
		data, _ := json.MarshalIndent(
			result.Object, "", "  ",
		)
		fmt.Println(string(data))
		return nil
	}

	phase, _, _ := unstructured.NestedString(
		result.Object,
		"status", "phase",
	)
	mdRef, _, _ := unstructured.NestedString(
		result.Object,
		"spec", "modelRegistrationRef",
	)

	w := tabwriter.NewWriter(
		os.Stdout, 0, 4, 2, ' ', 0,
	)
	fmt.Fprintln(w,
		"NAME\tMODEL-DEPLOYMENT\tPHASE")
	fmt.Fprintf(w, "%s\t%s\t%s\n",
		result.GetName(), mdRef, phase)
	w.Flush()

	return nil
}

func newModelDeploymentListCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "list",
		Short: "List ModelDeployment resources",
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelDeploymentList(
				cmd.Context(), namespace,
			)
		},
	}

	cmd.Flags().StringVarP(
		&namespace, "namespace", "n", "",
		"Kubernetes namespace",
	)
	return cmd
}

func runModelDeploymentList(
	ctx context.Context,
	namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if namespace == "" {
		namespace = defaultNamespaceFromKubeconfig()
	}

	list, err := client.Resource(modelDeploymentGVR).
		Namespace(namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return fmt.Errorf(
			"listing ModelDeployment: %w",
			err,
		)
	}

	w := tabwriter.NewWriter(
		os.Stdout, 0, 4, 2, ' ', 0,
	)
	fmt.Fprintln(w,
		"NAME\tMODEL-DEPLOYMENT\tPHASE")
	for _, item := range list.Items {
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		mdRef, _, _ := unstructured.NestedString(
			item.Object,
			"spec", "modelRegistrationRef",
		)
		fmt.Fprintf(w, "%s\t%s\t%s\n",
			item.GetName(), mdRef, phase)
	}
	w.Flush()
	return nil
}

func newModelDeploymentDeleteCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "delete <name>",
		Short: "Delete a ModelDeployment",
		Args:  cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelDeploymentDelete(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVarP(
		&namespace, "namespace", "n", "",
		"Kubernetes namespace",
	)
	return cmd
}

func runModelDeploymentDelete(
	ctx context.Context,
	name string,
	namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if namespace == "" {
		namespace = defaultNamespaceFromKubeconfig()
	}

	err = client.Resource(modelDeploymentGVR).
		Namespace(namespace).
		Delete(ctx, name, metav1.DeleteOptions{})
	if err != nil {
		return fmt.Errorf(
			"deleting ModelDeployment: %w",
			err,
		)
	}

	fmt.Printf(
		"ModelDeployment %q deleted\n", name,
	)
	return nil
}

// mdAllConditions is the ordered list of conditions
// tracked during model deployment instance setup.
var mdAllConditions = []string{
	"ModelRegistrationReady",
	"EndpointSubmitted",
	"InferenceServiceCreated",
	"EndpointDeployed",
}

// mdRemainingConditions returns conditions not yet
// True.
func mdRemainingConditions(
	done map[string]bool,
) []string {
	var remaining []string
	for _, c := range mdAllConditions {
		if !done[c] {
			remaining = append(remaining, c)
		}
	}
	return remaining
}

// isMDErrorReason returns true if the condition reason
// indicates a hard error that should be displayed with
// the ✗ indicator.
func isMDErrorReason(reason string) bool {
	switch reason {
	case "Failed", "Error", "ContainerError",
		"DeploymentTimeout":
		return true
	}
	return strings.Contains(reason, "Failed")
}

func newModelDeploymentStatusCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use: "status <name>",
		Short: "Show detailed status of a " +
			"ModelDeployment",
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelDeploymentStatus(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVarP(
		&namespace, "namespace", "n", "",
		"Kubernetes namespace",
	)
	return cmd
}

func runModelDeploymentStatus(
	ctx context.Context,
	name string,
	namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if namespace == "" {
		namespace = defaultNamespaceFromKubeconfig()
	}

	result, err := client.Resource(modelDeploymentGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting ModelDeployment: %w",
			err,
		)
	}

	phase, _, _ := unstructured.NestedString(
		result.Object, "status", "phase",
	)
	mdRef, _, _ := unstructured.NestedString(
		result.Object,
		"spec", "modelRegistrationRef",
	)

	fmt.Printf(
		"ModelDeployment: %s\n", name,
	)
	fmt.Printf(
		"  ModelRegistration: %s\n", mdRef,
	)
	fmt.Printf("  Phase:           %s\n", phase)

	if phase == "Failed" {
		msg, _, _ := unstructured.NestedString(
			result.Object, "status", "message",
		)
		if msg != "" {
			fmt.Printf(
				"  Message:         %s\n", msg,
			)
		}
	}

	traceId, _, _ := unstructured.NestedString(
		result.Object,
		"status", "lastOperationTraceId",
	)
	if traceId != "" {
		fmt.Printf(
			"  TraceId:         %s\n", traceId,
		)
	}

	endpoint, _, _ := unstructured.NestedString(
		result.Object, "status", "serviceEndpoint",
	)
	if endpoint != "" {
		fmt.Printf(
			"  Endpoint:        %s\n", endpoint,
		)
	}

	fmt.Println()

	// Print conditions.
	conditions, _, _ := unstructured.NestedSlice(
		result.Object, "status", "conditions",
	)
	if len(conditions) > 0 {
		fmt.Println("Conditions:")
		w := tabwriter.NewWriter(
			os.Stdout, 0, 4, 2, ' ', 0,
		)
		fmt.Fprintf(w,
			"  \tTYPE\tSTATUS\tREASON\tMESSAGE\n")
		for _, c := range conditions {
			cMap, ok := c.(map[string]interface{})
			if !ok {
				continue
			}
			cType, _ := cMap["type"].(string)
			cStatus, _ :=
				cMap["status"].(string)
			cReason, _ :=
				cMap["reason"].(string)
			cMsg, _ :=
				cMap["message"].(string)
			indicator := "?"
			if cStatus == "True" {
				indicator = "✓"
			} else if cStatus == "False" {
				if isMDErrorReason(cReason) {
					indicator = "✗"
				} else {
					indicator = "○"
				}
			}
			if len(cMsg) > 120 {
				cMsg = cMsg[:117] + "..."
			}
			fmt.Fprintf(w,
				"  %s\t%s\t%s\t%s\t%s\n",
				indicator, cType, cStatus,
				cReason, cMsg)
		}
		w.Flush()
	}

	return nil
}

func newModelDeploymentWaitCmd() *cobra.Command {
	var namespace string
	var forPhase string
	var timeout string

	cmd := &cobra.Command{
		Use: "wait <name>",
		Short: "Wait for a ModelDeployment " +
			"to reach a phase",
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			if namespace == "" {
				namespace =
					defaultNamespaceFromKubeconfig()
			}
			return runModelDeploymentWait(
				cmd.Context(), args[0],
				namespace, forPhase, timeout,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(&namespace, "namespace", "n", "",
		"Kubernetes namespace")
	f.StringVar(&forPhase, "for",
		"Ready", "Phase to wait for")
	f.StringVar(&timeout, "timeout",
		"600s", "Timeout duration")
	return cmd
}

func runModelDeploymentWait(
	ctx context.Context,
	name string,
	namespace string,
	targetPhase string,
	timeout string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	dur, err := time.ParseDuration(timeout)
	if err != nil {
		return fmt.Errorf(
			"invalid timeout %q: %w", timeout, err,
		)
	}

	ctx, cancel := context.WithTimeout(ctx, dur)
	defer cancel()

	start := time.Now()
	useColor := term.IsTerminal(
		int(os.Stdout.Fd()),
	)

	watcher, err := client.Resource(modelDeploymentGVR).
		Namespace(namespace).
		Watch(ctx, metav1.ListOptions{
			FieldSelector: "metadata.name=" + name,
		})
	if err != nil {
		return fmt.Errorf(
			"watching ModelDeployment: %w",
			err,
		)
	}
	defer watcher.Stop()

	// Watch Kubernetes Events for this
	// ModelDeployment.
	eventsCh := make(chan watch.Event)
	evWatcher, evErr := startMDEventWatcher(
		ctx, namespace, name, eventsCh,
	)
	if evErr == nil {
		defer evWatcher.Stop()
	}

	fmt.Printf(
		"Waiting for ModelDeployment %q "+
			"to reach %s...\n",
		name, targetPhase,
	)

	printedConditions := make(map[string]string)
	doneConditions := make(map[string]bool)
	printedEvents := make(map[string]bool)
	lastPhase := ""

	for {
		select {
		case <-ctx.Done():
			return fmt.Errorf(
				"timed out waiting for "+
					"ModelDeployment %q "+
					"to reach %s after %s",
				name, targetPhase,
				time.Since(start).
					Round(time.Second),
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
			msg := ev.Message
			key := ev.Reason + ":" + msg
			if printedEvents[key] {
				continue
			}
			printedEvents[key] = true
			elapsed := time.Since(start).
				Round(time.Second)
			fmt.Printf(
				"  %-8s · %s\n",
				elapsed, msg,
			)
		case event, ok := <-watcher.ResultChan():
			if !ok {
				return fmt.Errorf(
					"watch channel closed",
				)
			}
			if event.Type != watch.Modified &&
				event.Type != watch.Added {
				continue
			}
			obj, ok := event.Object.(*unstructured.Unstructured)
			if !ok {
				continue
			}

			p, _, _ := unstructured.NestedString(
				obj.Object, "status", "phase",
			)

			// Print phase transitions.
			if p != "" && p != lastPhase {
				elapsed := time.Since(start).
					Round(time.Second)
				if p == "Deploying" {
					fmt.Printf(
						"  %-8s %s\n",
						elapsed,
						"Deploying started")
				} else if p != "Ready" &&
					p != "Failed" {
					fmt.Printf(
						"  %-8s Phase: %s\n",
						elapsed, p)
				}
				lastPhase = p
			}

			// Print condition updates.
			conditions, _, _ :=
				unstructured.NestedSlice(
					obj.Object,
					"status", "conditions",
				)
			for _, c := range conditions {
				cMap, ok :=
					c.(map[string]interface{})
				if !ok {
					continue
				}
				cType, _ :=
					cMap["type"].(string)
				cStatus, _ :=
					cMap["status"].(string)
				cMsg, _ :=
					cMap["message"].(string)

				elapsed := time.Since(start).
					Round(time.Second)

				if cStatus == "True" {
					key := cType + ":✓"
					if printedConditions[cType] ==
						key {
						continue
					}
					printedConditions[cType] = key
					doneConditions[cType] = true
					waitingOn :=
						mdRemainingConditions(
							doneConditions,
						)
					line := ""
					if len(waitingOn) > 0 {
						line = fmt.Sprintf(
							"  %-8s ✓ %s"+
								" (waiting: %s)",
							elapsed, cType,
							strings.Join(
								waitingOn,
								", ",
							),
						)
					} else {
						line = fmt.Sprintf(
							"  %-8s ✓ %s",
							elapsed, cType,
						)
					}
					fmt.Println(line)
				} else if cStatus == "False" {
					msg := cMsg
					if len(msg) > 120 {
						msg = msg[:117] + "..."
					}
					cReason, _ :=
						cMap["reason"].(string)
					icon := "○"
					if isMDErrorReason(cReason) {
						icon = "✗"
					}
					key := cType + ":" + icon
					if printedConditions[cType] ==
						key {
						continue
					}
					printedConditions[cType] = key
					line := fmt.Sprintf(
						"  %-8s %s %s - %s",
						elapsed, icon,
						cType, msg,
					)
					if useColor &&
						icon == "✗" {
						fmt.Printf(
							"%s%s%s\n",
							colorRed, line,
							colorReset,
						)
					} else {
						fmt.Println(line)
					}
				}
			}

			if p == targetPhase {
				elapsed := time.Since(start).
					Round(time.Second)
				traceId, _, _ :=
					unstructured.NestedString(
						obj.Object,
						"status",
						"lastOperationTraceId",
					)
				if traceId != "" {
					fmt.Printf(
						"ModelDeployment"+
							" %q reached %s "+
							"(%s, trace: %s)\n",
						name, targetPhase,
						elapsed, traceId,
					)
				} else {
					fmt.Printf(
						"ModelDeployment"+
							" %q reached %s "+
							"(%s)\n",
						name, targetPhase,
						elapsed,
					)
				}
				return nil
			}
			if p == "Failed" &&
				targetPhase != "Failed" {
				elapsed := time.Since(start).
					Round(time.Second)
				msg, _, _ :=
					unstructured.NestedString(
						obj.Object,
						"status", "message",
					)
				traceId, _, _ :=
					unstructured.NestedString(
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
						"ModelDeployment"+
							" %q failed after "+
							"%s: %s%s",
						name, elapsed,
						msg, traceInfo,
					)
				}
				return fmt.Errorf(
					"ModelDeployment "+
						"%q failed after %s%s",
					name, elapsed, traceInfo,
				)
			}
		}
	}
}

func newModelDeploymentReconcileCmd() *cobra.Command {
	var namespace string
	var noWait bool
	var timeout string

	cmd := &cobra.Command{
		Use:     "reconcile <name>",
		Aliases: []string{"retry"},
		Short: "Trigger reconciliation of a " +
			"ModelDeployment",
		Long: `Force the operator to re-evaluate the
ModelDeployment. Sets the
reconcile.cleanroom.azure.com/requestedAt
annotation and optionally waits for the resource
to reach Ready.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelDeploymentReconcile(
				cmd.Context(), args[0],
				namespace, !noWait, timeout,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(&namespace, "namespace", "n", "",
		"Kubernetes namespace")
	f.BoolVar(&noWait, "no-wait", false,
		"Do not wait for Ready after triggering "+
			"reconciliation")
	f.StringVar(&timeout, "timeout", "600s",
		"Timeout when waiting")
	return cmd
}

func runModelDeploymentReconcile(
	ctx context.Context,
	name string,
	namespace string,
	wait bool,
	timeout string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if namespace == "" {
		namespace = defaultNamespaceFromKubeconfig()
	}

	mdi, err := client.Resource(modelDeploymentGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting ModelDeployment: %w",
			err,
		)
	}

	phase, _, _ := unstructured.NestedString(
		mdi.Object, "status", "phase",
	)

	requestedAt := time.Now().UTC().Format(
		time.RFC3339Nano,
	)
	annotations := mdi.GetAnnotations()
	if annotations == nil {
		annotations = map[string]string{}
	}
	annotations["reconcile.cleanroom.azure.com/"+
		"requestedAt"] = requestedAt
	mdi.SetAnnotations(annotations)

	_, err = client.Resource(modelDeploymentGVR).
		Namespace(namespace).
		Update(ctx, mdi, metav1.UpdateOptions{})
	if err != nil {
		return fmt.Errorf(
			"setting reconcile annotation: %w", err,
		)
	}

	fmt.Printf(
		"► reconciliation triggered for "+
			"ModelDeployment %q (was: %s)\n",
		name, phase,
	)

	if wait {
		dur, err := time.ParseDuration(timeout)
		if err != nil {
			return fmt.Errorf(
				"invalid timeout %q: %w",
				timeout, err,
			)
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
						"controller to " +
						"acknowledge reconcile " +
						"request",
				)
			case <-ticker.C:
				latest, err := client.Resource(
					modelDeploymentGVR,
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
					return runModelDeploymentWait(
						ctx, name, namespace,
						"Ready", timeout,
					)
				}
			}
		}
	}

	return nil
}

// startMDEventWatcher watches Kubernetes Events for a
// specific ModelDeployment and forwards them to
// the provided channel. Only new events are forwarded
// (history is skipped via resourceVersion).
func startMDEventWatcher(
	ctx context.Context,
	namespace string,
	mdName string,
	ch chan<- watch.Event,
) (watch.Interface, error) {
	clientset, err := getClientset()
	if err != nil {
		return nil, err
	}

	fieldSel := fmt.Sprintf(
		"involvedObject.name=%s,"+
			"involvedObject.kind="+
			"ModelDeployment",
		mdName,
	)

	// List to get current resourceVersion, then
	// watch only new events.
	eventList, err := clientset.CoreV1().
		Events(namespace).
		List(ctx, metav1.ListOptions{
			FieldSelector: fieldSel,
		})
	if err != nil {
		return nil, err
	}

	w, err := clientset.CoreV1().
		Events(namespace).
		Watch(ctx, metav1.ListOptions{
			FieldSelector:   fieldSel,
			ResourceVersion: eventList.ResourceVersion,
		})
	if err != nil {
		return nil, err
	}

	go func() {
		defer close(ch)
		for ev := range w.ResultChan() {
			ch <- ev
		}
	}()

	return w, nil
}
