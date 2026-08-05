package app

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/spf13/cobra"

	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
)

const (
	// bootstrapClusterName is the fixed, hidden Kind cluster
	// name used by the bootstrap runtime. It is an internal
	// implementation detail and not surfaced to users.
	bootstrapClusterName = "cleanroom-bootstrap"

	// bootstrapNamespace is where the cluster-provider-client
	// runs inside the bootstrap cluster.
	bootstrapNamespace = "cleanroom-system"
)

type bootstrapStartOpts struct {
	registryName string
	registryPort string
	envFiles     []string
	pullPolicy   string
}

type bootstrapClusterCreateOpts struct {
	infraType       string
	providerConfig  string
	kindClusterName string
	aksClusterName  string
	timeout         string
	enableAad       bool
}

type bootstrapKubeconfigOpts struct {
	output string
}

func newBootstrapCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use: "bootstrap",
		Short: "Manage the ephemeral bootstrap runtime that " +
			"provisions workload clusters",
		Long: `The bootstrap runtime is a hidden, ephemeral
cluster whose only job is to provision workload clusters. Once
a workload cluster is created you install the cleanroom operator
onto it directly; the bootstrap runtime can then be stopped.`,
	}
	cmd.AddCommand(newBootstrapStartCmd())
	cmd.AddCommand(newBootstrapStopCmd())
	cmd.AddCommand(newBootstrapKubeconfigCmd())
	cmd.AddCommand(newBootstrapClusterCmd())
	return cmd
}

func newBootstrapStartCmd() *cobra.Command {
	o := &bootstrapStartOpts{}
	cmd := &cobra.Command{
		Use:   "start",
		Short: "Start the bootstrap runtime",
		Long: `Creates the hidden bootstrap cluster and installs
only the cluster-provider-client (plus credentials-proxy). The
provided --env-file image references are stored so subsequent
'bootstrap cluster create' commands can provision workload
clusters.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			return runBootstrapStart(cmd.Context(), o)
		},
	}
	f := cmd.Flags()
	f.StringVar(&o.registryName, "registry-name",
		defaultRegistryName, "Local Docker registry name")
	f.StringVar(&o.registryPort, "registry-port",
		defaultRegistryPort, "Local Docker registry port")
	f.StringSliceVar(&o.envFiles, "env-file", nil,
		"Path to KEY=VALUE env file with image "+
			"references (repeatable, required)")
	f.StringVar(&o.pullPolicy, "image-pull-policy", "",
		"Image pull policy (Always, IfNotPresent, Never)")
	return cmd
}

func newBootstrapStopCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "stop",
		Short: "Stop and delete the bootstrap runtime",
		Long: `Deletes the hidden bootstrap cluster. Workload
clusters created by the bootstrap runtime are independent and
continue running.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			return runBootstrapStop(cmd.Context())
		},
	}
	return cmd
}

func newBootstrapClusterCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "cluster",
		Short: "Manage workload clusters via the bootstrap runtime",
	}
	cmd.AddCommand(newBootstrapClusterCreateCmd())
	cmd.AddCommand(newBootstrapClusterKubeconfigCmd())
	return cmd
}

func newBootstrapKubeconfigCmd() *cobra.Command {
	o := &bootstrapKubeconfigOpts{}
	cmd := &cobra.Command{
		Use:   "kubeconfig",
		Short: "Fetch the bootstrap runtime's own kubeconfig",
		Long: `Fetches the kubeconfig for the hidden bootstrap
cluster itself. This is primarily a diagnostic aid for inspecting
the cluster-provider-client; day-to-day workflows do not need it.`,
		Args: cobra.NoArgs,
		RunE: func(cmd *cobra.Command, args []string) error {
			return runBootstrapSelfKubeconfig(o)
		},
	}
	cmd.Flags().StringVarP(&o.output, "file", "f", "",
		"Path to write the kubeconfig (defaults to stdout)")
	return cmd
}

func newBootstrapClusterCreateCmd() *cobra.Command {
	o := &bootstrapClusterCreateOpts{}
	cmd := &cobra.Command{
		Use:   "create <name>",
		Short: "Provision a bare workload cluster",
		Long: `Provisions a bare workload cluster (correct
networking and node configuration) and nothing else. Workload
profiles such as inferencing depend on CCF and governance, which
are set up later by the cleanroom operator on the workload
cluster itself (via 'environment create'), not by the bootstrap
runtime.`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runBootstrapClusterCreate(
				cmd.Context(), args[0], o,
			)
		},
	}
	f := cmd.Flags()
	f.StringVar(&o.infraType, "type", "virtual",
		"Infrastructure type (virtual or aks)")
	f.StringVar(&o.providerConfig, "provider-config", "",
		"Path to provider config JSON file "+
			"(required for aks)")
	f.StringVar(&o.kindClusterName, "kind-cluster-name", "",
		"Override the kind cluster name (virtual)")
	f.StringVar(&o.aksClusterName, "aks-cluster-name", "",
		"Override the AKS cluster name (aks)")
	f.BoolVar(&o.enableAad, "enable-aad", true,
		"Enable AKS-managed Azure AD integration at cluster "+
			"creation (aks). Enabled by default so the cluster "+
			"does not need a slow AAD-enabling update later when "+
			"an environment is created.")
	f.StringVar(&o.timeout, "timeout", "1800s",
		"Timeout waiting for cluster creation")
	return cmd
}

func newBootstrapClusterKubeconfigCmd() *cobra.Command {
	o := &bootstrapKubeconfigOpts{}
	cmd := &cobra.Command{
		Use:   "kubeconfig <name>",
		Short: "Fetch the workload cluster kubeconfig",
		Long: `Fetches the external-form kubeconfig for a workload
cluster. The kubeconfig points at an externally reachable
address so it remains valid after 'bootstrap stop'.`,
		Args: cobra.ExactArgs(1),
		RunE: func(cmd *cobra.Command, args []string) error {
			return runBootstrapKubeconfig(
				cmd.Context(), args[0], o,
			)
		},
	}
	cmd.Flags().StringVarP(&o.output, "file", "f", "",
		"Path to write the kubeconfig (defaults to stdout)")
	return cmd
}

func runBootstrapStart(
	ctx context.Context,
	o *bootstrapStartOpts,
) error {
	if err := checkPrereqs("docker", "kubectl"); err != nil {
		return err
	}
	if len(o.envFiles) == 0 {
		return fmt.Errorf("--env-file is required")
	}

	// Derive image references from env files. Only the
	// cluster-provider-client image is strictly required.
	install := providerDeployOpts{
		namespace:       bootstrapNamespace,
		envFiles:        o.envFiles,
		imagePullPolicy: o.pullPolicy,
		// Bootstrap runs ONLY the cluster-provider-client
		// (+ credentials-proxy). Everything else is disabled.
		operatorDisabled:    true,
		ccfProviderDisabled: true,
		localIdpDisabled:    true,
		credentialsProxy:    true,
		enableTelemetry:     false,
	}
	for _, ef := range o.envFiles {
		if err := applyEnvFileDefaults(ef, &install); err != nil {
			return err
		}
	}

	provider := newKindProvider()

	fmt.Printf(
		"[1/3] Creating bootstrap cluster '%s'...",
		bootstrapClusterName,
	)
	start := time.Now()
	if err := ensureKindCluster(
		provider, bootstrapClusterName,
	); err != nil {
		fmt.Println()
		return fmt.Errorf("creating bootstrap cluster: %w", err)
	}
	if err := configureContainerdRegistry(
		provider, bootstrapClusterName,
		o.registryName, o.registryPort,
	); err != nil {
		fmt.Println()
		return fmt.Errorf(
			"configuring containerd registry: %w", err,
		)
	}
	if err := connectRegistryToKind(o.registryName); err != nil {
		fmt.Println()
		return fmt.Errorf(
			"connecting registry to bootstrap network: %w", err,
		)
	}
	fmt.Printf(" done (%s)\n", formatElapsed(start))

	// Write kubeconfig for the install step.
	fmt.Print("[2/3] Writing bootstrap kubeconfig...")
	kubeconfig, err := provider.KubeConfig(
		bootstrapClusterName, false,
	)
	if err != nil {
		fmt.Println()
		return fmt.Errorf("getting kubeconfig: %w", err)
	}
	tmpFile, err := os.CreateTemp(
		"", "bootstrap-kubeconfig-*.yaml",
	)
	if err != nil {
		fmt.Println()
		return fmt.Errorf("creating temp kubeconfig: %w", err)
	}
	if _, err := tmpFile.WriteString(kubeconfig); err != nil {
		fmt.Println()
		return fmt.Errorf("writing temp kubeconfig: %w", err)
	}
	tmpFile.Close()
	kubeconfigPath = tmpFile.Name()
	fmt.Println(" done")

	// Install only the cluster-provider-client.
	fmt.Println("[3/3] Installing cluster-provider-client...")
	if err := runProviderDeploy(ctx, &install); err != nil {
		return fmt.Errorf(
			"installing cluster-provider-client: %w", err,
		)
	}

	// Persist the env files in a ConfigMap so later commands
	// can reference the same image set without re-passing
	// --env-file.
	if err := storeBootstrapEnvConfigMap(
		ctx, o.envFiles,
	); err != nil {
		fmt.Printf(
			"Warning: storing env config: %v\n", err,
		)
	}

	fmt.Println()
	fmt.Println("Bootstrap runtime is ready.")
	fmt.Println(
		"Next: kubectl cleanroom bootstrap cluster create " +
			"<name> --type virtual",
	)
	return nil
}

func runBootstrapStop(ctx context.Context) error {
	if err := checkPrereqs("docker"); err != nil {
		return err
	}
	provider := newKindProvider()
	fmt.Printf(
		"Deleting bootstrap cluster '%s'...\n",
		bootstrapClusterName,
	)
	if err := provider.Delete(bootstrapClusterName, ""); err != nil {
		fmt.Printf("Warning: deleting cluster: %v\n", err)
	}
	fmt.Println("Bootstrap runtime stopped. Workload clusters " +
		"continue running.")
	return nil
}

func runBootstrapSelfKubeconfig(
	o *bootstrapKubeconfigOpts,
) error {
	if err := checkPrereqs("docker"); err != nil {
		return err
	}
	provider := newKindProvider()
	kubeconfig, err := provider.KubeConfig(
		bootstrapClusterName, false,
	)
	if err != nil {
		return fmt.Errorf(
			"bootstrap runtime not found; run "+
				"'kubectl cleanroom bootstrap start' "+
				"first: %w",
			err,
		)
	}

	if o.output == "" {
		fmt.Print(kubeconfig)
		return nil
	}
	if err := os.WriteFile(
		o.output, []byte(kubeconfig), 0600,
	); err != nil {
		return fmt.Errorf(
			"writing kubeconfig to %s: %w", o.output, err,
		)
	}
	abs, _ := filepath.Abs(o.output)
	fmt.Printf("Wrote bootstrap kubeconfig to %s\n", abs)
	return nil
}

func runBootstrapClusterCreate(
	ctx context.Context,
	name string,
	o *bootstrapClusterCreateOpts,
) error {
	if err := checkPrereqs("kubectl"); err != nil {
		return err
	}
	if err := ensureBootstrapKubeconfig(); err != nil {
		return err
	}

	// Build providerConfig with the name override.
	pc := map[string]interface{}{}
	if o.providerConfig != "" {
		data, err := os.ReadFile(o.providerConfig)
		if err != nil {
			return fmt.Errorf(
				"reading provider config: %w", err,
			)
		}
		if err := json.Unmarshal(data, &pc); err != nil {
			return fmt.Errorf(
				"parsing provider config JSON: %w", err,
			)
		}
	}
	if o.kindClusterName != "" {
		pc["kindClusterName"] = o.kindClusterName
	}
	if o.aksClusterName != "" {
		pc["aksClusterName"] = o.aksClusterName
	}
	pcRaw, err := json.Marshal(pc)
	if err != nil {
		return fmt.Errorf("marshaling provider config: %w", err)
	}

	// Bootstrap only ever provisions a bare cluster.
	// Workload profiles (inferencing, analytics) depend on
	// CCF/governance and are applied later by the operator
	// on the workload cluster via 'environment create'.
	input := &client.PutClusterInput{
		InfraType:      o.infraType,
		ProviderConfig: pcRaw,
	}

	// Enable AKS-managed AAD integration up-front (default) so
	// the cluster is created with AAD already on. Otherwise a
	// later 'environment create' would trigger a slow
	// AAD-enabling cluster update. Only applies to aks.
	if o.infraType == "aks" && o.enableAad {
		input.AadProfile = &client.AadProfileInput{
			Enabled: true,
		}
	}

	cc, stop, err := dialProviderClient(ctx)
	if err != nil {
		return err
	}
	defer stop()

	// Persist the cluster's infraType + providerConfig so later
	// commands (e.g. kubeconfig) can locate the cluster without
	// the user re-supplying them.
	if err := storeBootstrapClusterConfig(
		name, o.infraType, pcRaw,
	); err != nil {
		fmt.Printf(
			"Warning: storing cluster config: %v\n", err,
		)
	}

	fmt.Printf("Provisioning workload cluster '%s'...\n", name)
	opLocation, err := cc.CreateCluster(ctx, name, input)
	if err != nil {
		return fmt.Errorf("creating cluster: %w", err)
	}

	if opLocation == "" {
		fmt.Println("Cluster created.")
		return nil
	}

	timeout, err := time.ParseDuration(o.timeout)
	if err != nil {
		timeout = 30 * time.Minute
	}
	return pollBootstrapOperation(ctx, cc, opLocation, timeout)
}

func runBootstrapKubeconfig(
	ctx context.Context,
	name string,
	o *bootstrapKubeconfigOpts,
) error {
	if err := checkPrereqs("kubectl"); err != nil {
		return err
	}
	if err := ensureBootstrapKubeconfig(); err != nil {
		return err
	}

	cc, stop, err := dialProviderClient(ctx)
	if err != nil {
		return err
	}
	defer stop()

	// Load the stored infraType + providerConfig for this
	// cluster so we can locate it (required for AKS).
	infraType, pcRaw, loadErr := loadBootstrapClusterConfig(name)
	if loadErr != nil {
		return fmt.Errorf(
			"loading cluster config for %q "+
				"(was it created via bootstrap?): %w",
			name, loadErr,
		)
	}

	// Request the external-form kubeconfig (internal=false)
	// so it remains reachable from the host after the
	// bootstrap runtime is stopped.
	kc, err := cc.GetKubeconfig(ctx, name, &client.GetKubeconfigInput{
		InfraType:      infraType,
		ProviderConfig: pcRaw,
		AccessRole:     "admin",
		Internal:       false,
	})
	if err != nil {
		return fmt.Errorf("fetching kubeconfig: %w", err)
	}

	if o.output == "" {
		fmt.Print(string(kc))
		return nil
	}
	if err := os.WriteFile(o.output, kc, 0600); err != nil {
		return fmt.Errorf(
			"writing kubeconfig to %s: %w", o.output, err,
		)
	}
	abs, _ := filepath.Abs(o.output)
	fmt.Printf("Wrote workload kubeconfig to %s\n", abs)
	return nil
}

// dialProviderClient sets the bootstrap kubeconfig, port-forwards
// to the cluster-provider-client service, and returns a REST
// client plus a stop function to tear down the port-forward.
func dialProviderClient(
	ctx context.Context,
) (*client.ClusterClient, func(), error) {
	if err := ensureBootstrapKubeconfig(); err != nil {
		return nil, nil, err
	}

	pf, localPort, err := startPortForwardToPort(
		bootstrapNamespace,
		"svc/cluster-provider-client",
		8080, 8080,
	)
	if err != nil {
		return nil, nil, fmt.Errorf(
			"port-forwarding to cluster-provider-client: %w",
			err,
		)
	}
	stop := func() {
		if pf != nil && pf.Process != nil {
			_ = pf.Process.Kill()
		}
	}

	endpoint := fmt.Sprintf("http://localhost:%d", localPort)
	cc := client.NewClusterClient(endpoint)

	// Wait for the provider client to be ready.
	deadline := time.Now().Add(30 * time.Second)
	for {
		if err := cc.CheckReady(ctx); err == nil {
			break
		}
		if time.Now().After(deadline) {
			stop()
			return nil, nil, fmt.Errorf(
				"cluster-provider-client not ready",
			)
		}
		time.Sleep(2 * time.Second)
	}

	return cc, stop, nil
}

func pollBootstrapOperation(
	ctx context.Context,
	cc *client.ClusterClient,
	opLocation string,
	timeout time.Duration,
) error {
	deadline := time.Now().Add(timeout)
	lastProgress := ""
	for {
		op, err := cc.GetOperation(ctx, opLocation)
		if err != nil {
			return fmt.Errorf("polling operation: %w", err)
		}
		if op == nil {
			return fmt.Errorf("operation not found")
		}

		if len(op.Progress) > 0 {
			latest := op.Progress[len(op.Progress)-1]
			if latest != lastProgress {
				fmt.Printf("  %s\n", latest)
				lastProgress = latest
			}
		}

		switch op.Status {
		case "Succeeded":
			fmt.Println("Workload cluster provisioned.")
			return nil
		case "Failed":
			return fmt.Errorf(
				"cluster provisioning failed: %s",
				string(op.Error),
			)
		}

		if time.Now().After(deadline) {
			return fmt.Errorf(
				"timed out waiting for cluster provisioning",
			)
		}
		time.Sleep(5 * time.Second)
	}
}

// ensureBootstrapKubeconfig points kubeconfigPath at the hidden
// bootstrap cluster.
func ensureBootstrapKubeconfig() error {
	provider := newKindProvider()
	kubeconfig, err := provider.KubeConfig(
		bootstrapClusterName, false,
	)
	if err != nil {
		return fmt.Errorf(
			"bootstrap runtime not found; run "+
				"'kubectl cleanroom bootstrap start' first: %w",
			err,
		)
	}
	tmpFile, err := os.CreateTemp(
		"", "bootstrap-kubeconfig-*.yaml",
	)
	if err != nil {
		return fmt.Errorf("creating temp kubeconfig: %w", err)
	}
	if _, err := tmpFile.WriteString(kubeconfig); err != nil {
		return fmt.Errorf("writing temp kubeconfig: %w", err)
	}
	tmpFile.Close()
	kubeconfigPath = tmpFile.Name()
	return nil
}

// storeBootstrapEnvConfigMap persists the env file contents in a
// ConfigMap so later commands can reference the same image set.
func storeBootstrapEnvConfigMap(
	ctx context.Context,
	envFiles []string,
) error {
	merged := map[string]string{}
	for _, ef := range envFiles {
		raw, err := readRawEnvFile(ef)
		if err != nil {
			return err
		}
		for k, v := range raw {
			merged[k] = v
		}
	}
	// Best-effort: store as JSON in a ConfigMap via kubectl.
	data, err := json.Marshal(merged)
	if err != nil {
		return err
	}
	tmp, err := os.CreateTemp("", "bootstrap-env-*.json")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.Write(data); err != nil {
		return err
	}
	tmp.Close()

	// Best-effort: create/replace the ConfigMap.
	_ = runCmd(
		"kubectl", "--kubeconfig", kubeconfigPath,
		"-n", bootstrapNamespace,
		"delete", "configmap", "bootstrap-env",
		"--ignore-not-found",
	)
	return runCmd(
		"kubectl", "--kubeconfig", kubeconfigPath,
		"-n", bootstrapNamespace,
		"create", "configmap", "bootstrap-env",
		"--from-file=env.json="+tmp.Name(),
	)
}

// bootstrapClusterConfigMapName returns the ConfigMap name used
// to persist a workload cluster's infraType + providerConfig.
func bootstrapClusterConfigMapName(name string) string {
	return "bootstrap-cluster-" + name
}

// storeBootstrapClusterConfig persists a workload cluster's
// infraType and providerConfig in a ConfigMap so later commands
// (e.g. kubeconfig) can locate the cluster without the user
// re-supplying them.
func storeBootstrapClusterConfig(
	name string,
	infraType string,
	providerConfig []byte,
) error {
	tmp, err := os.CreateTemp("", "bootstrap-cluster-pc-*.json")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.Write(providerConfig); err != nil {
		return err
	}
	tmp.Close()

	cmName := bootstrapClusterConfigMapName(name)
	_ = runCmd(
		"kubectl", "--kubeconfig", kubeconfigPath,
		"-n", bootstrapNamespace,
		"delete", "configmap", cmName,
		"--ignore-not-found",
	)
	return runCmd(
		"kubectl", "--kubeconfig", kubeconfigPath,
		"-n", bootstrapNamespace,
		"create", "configmap", cmName,
		"--from-literal=infraType="+infraType,
		"--from-file=providerConfig="+tmp.Name(),
	)
}

// loadBootstrapClusterConfig reads back a workload cluster's
// infraType and providerConfig from its ConfigMap.
func loadBootstrapClusterConfig(
	name string,
) (infraType string, providerConfig []byte, err error) {
	cmName := bootstrapClusterConfigMapName(name)
	infraType, err = runCmdOutput(
		"kubectl", "--kubeconfig", kubeconfigPath,
		"-n", bootstrapNamespace,
		"get", "configmap", cmName,
		"-o", "jsonpath={.data.infraType}",
	)
	if err != nil {
		return "", nil, err
	}
	infraType = strings.TrimSpace(infraType)

	pcStr, err := runCmdOutput(
		"kubectl", "--kubeconfig", kubeconfigPath,
		"-n", bootstrapNamespace,
		"get", "configmap", cmName,
		"-o", "jsonpath={.data.providerConfig}",
	)
	if err != nil {
		return "", nil, err
	}
	pcStr = strings.TrimSpace(pcStr)
	if pcStr == "" {
		return infraType, nil, nil
	}
	return infraType, []byte(pcStr), nil
}
