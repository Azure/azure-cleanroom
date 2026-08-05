package app

import (
	"bufio"
	"bytes"
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"

	"github.com/spf13/cobra"
	kindconfig "sigs.k8s.io/kind/pkg/apis/config/v1alpha4"
	kindcluster "sigs.k8s.io/kind/pkg/cluster"
)

const (
	defaultClusterName  = "cleanroom-mgmt"
	defaultRegistryName = "ccr-registry"
	defaultRegistryPort = "5000"
)

type devUpOpts struct {
	clusterName  string
	registryName string
	registryPort string
	output       string
	wait         bool
	timeout      string

	// install opts (forwarded to runProviderDeploy).
	installOpts providerDeployOpts
}

type devDownOpts struct {
	clusterName string
	save        bool
}

func newDevCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "dev",
		Short: "Local development environment lifecycle",
	}
	cmd.AddCommand(newDevUpCmd())
	cmd.AddCommand(newDevDownCmd())
	cmd.AddCommand(newDevSaveCmd())
	cmd.AddCommand(newDevRestoreCmd())
	cmd.AddCommand(newDevCollectLogsCmd())
	cmd.AddCommand(newDevAspireDashboardCmd())
	cmd.AddCommand(newDevDashboardCmd())
	cmd.AddCommand(newDevGenerateEnvCmd())
	return cmd
}

func newDevUpCmd() *cobra.Command {
	o := &devUpOpts{}

	cmd := &cobra.Command{
		Use:   "up",
		Short: "Create a local Kind management cluster and install the operator",
		Long: `Sets up a local development environment by:
1. Creating a Kind cluster with docker.sock access
2. Configuring containerd to pull from the local registry
3. Installing the cleanroom-operator via Helm

The local Docker registry must already be running (e.g. started
by the build scripts). Use --registry-name and --registry-port
to match your registry configuration.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			return runDevUp(cmd, o)
		},
	}

	f := cmd.Flags()
	f.StringVar(
		&o.clusterName, "name", defaultClusterName,
		"Name for the Kind management cluster",
	)
	f.StringVar(
		&o.registryName, "registry-name",
		defaultRegistryName,
		"Docker registry container name",
	)
	f.StringVar(
		&o.registryPort, "registry-port",
		defaultRegistryPort,
		"Host port for the local Docker registry",
	)
	f.StringVarP(
		&o.output, "output", "o", "",
		"Path to write the management cluster kubeconfig "+
			"(printed to stdout if empty)",
	)
	f.StringVar(
		&o.installOpts.imagePullPolicy,
		"image-pull-policy", "",
		"Image pull policy (Always, IfNotPresent, Never)",
	)
	f.StringSliceVar(
		&o.installOpts.envFiles,
		"env-file", nil,
		"Path to KEY=VALUE env file for provider client config "+
			"(repeatable)",
	)
	f.BoolVar(
		&o.installOpts.credentialsProxy,
		"credentials-proxy", true,
		"Enable credentials-proxy sidecar for Azure auth",
	)
	f.BoolVar(
		&o.installOpts.enableTelemetry,
		"enable-telemetry", true,
		"Enable OTLP tracing with aspire-dashboard",
	)
	f.StringVar(
		&o.installOpts.namespace,
		"namespace", operatorNamespace,
		"Kubernetes namespace",
	)
	f.StringSliceVar(
		&o.installOpts.setValues,
		"set", nil,
		"Set Helm values (key=value, repeatable)",
	)
	f.StringSliceVar(
		&o.installOpts.valuesFiles,
		"values", nil,
		"Path to YAML file with Helm value overrides",
	)
	f.BoolVar(
		&o.wait, "wait", false,
		"Wait for operator and provider pods to be ready",
	)
	f.StringVar(
		&o.timeout, "timeout", "120s",
		"Timeout when using --wait",
	)

	return cmd
}

func newDevDownCmd() *cobra.Command {
	o := &devDownOpts{}

	cmd := &cobra.Command{
		Use:   "down",
		Short: "Tear down the local Kind management cluster",
		Long: `Deletes the Kind cluster. The local Docker registry
is left running so it can be reused.

Use --save to automatically export all environment key bundles
before deletion. The saved files can be used with 'dev restore'
after recreating the cluster.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			return runDevDown(cmd.Context(), o)
		},
	}

	f := cmd.Flags()
	f.StringVar(
		&o.clusterName, "name", defaultClusterName,
		"Name of the Kind cluster to delete",
	)
	f.BoolVar(
		&o.save, "save", false,
		"Save key bundles for all environments before "+
			"tearing down",
	)
	return cmd
}

// kindClusterConfig returns the v1alpha4.Cluster config for the
// management cluster. The docker.sock mount enables the provider
// client to manage child Kind clusters from within the mgmt cluster.
// The ~/.azure mount enables the credentials-proxy sidecar to
// access Azure CLI credentials.
func kindClusterConfig() *kindconfig.Cluster {
	homeDir, _ := os.UserHomeDir()
	azureDir := homeDir + "/.azure"

	return &kindconfig.Cluster{
		TypeMeta: kindconfig.TypeMeta{
			Kind:       "Cluster",
			APIVersion: "kind.x-k8s.io/v1alpha4",
		},
		ContainerdConfigPatches: []string{
			`[plugins."io.containerd.grpc.v1.cri".registry]
  config_path = "/etc/containerd/certs.d"`,
		},
		Nodes: []kindconfig.Node{
			{
				Role: kindconfig.ControlPlaneRole,
				ExtraMounts: []kindconfig.Mount{
					{
						HostPath:      "/var/run/docker.sock",
						ContainerPath: "/var/run/docker.sock",
					},
					{
						HostPath:      azureDir,
						ContainerPath: azureDir,
						Readonly:      true,
					},
					{
						HostPath:      "/tmp/ccf-workspace",
						ContainerPath: "/tmp/ccf-workspace",
					},
				},
			},
		},
	}
}

// newKindProvider creates a kind cluster.Provider backed by Docker.
func newKindProvider() *kindcluster.Provider {
	return kindcluster.NewProvider(
		kindcluster.ProviderWithDocker(),
	)
}

func checkPrereqs(cmds ...string) error {
	var missing []string
	for _, c := range cmds {
		if _, err := exec.LookPath(c); err != nil {
			missing = append(missing, c)
		}
	}
	if len(missing) > 0 {
		return fmt.Errorf(
			"required tools not found on PATH: %s",
			strings.Join(missing, ", "),
		)
	}
	return nil
}

func runDevUp(cmd *cobra.Command, o *devUpOpts) error {
	if err := checkPrereqs("docker"); err != nil {
		return err
	}

	// Require --env-file.
	if len(o.installOpts.envFiles) == 0 {
		return fmt.Errorf("--env-file is required")
	}

	// Derive operator and provider-client image references
	// from the env files.
	for _, ef := range o.installOpts.envFiles {
		if err := applyEnvFileDefaults(
			ef, &o.installOpts,
		); err != nil {
			return err
		}
	}

	provider := newKindProvider()

	// 1. Create Kind cluster if it doesn't exist.
	fmt.Printf("[1/3] Creating Kind cluster '%s'...", o.clusterName)
	start := time.Now()
	if err := ensureKindCluster(
		provider, o.clusterName,
	); err != nil {
		fmt.Println()
		return fmt.Errorf("creating Kind cluster: %w", err)
	}

	// 2. Configure containerd on each node to use the registry.
	if err := configureContainerdRegistry(
		provider, o.clusterName,
		o.registryName, o.registryPort,
	); err != nil {
		fmt.Println()
		return fmt.Errorf(
			"configuring containerd registry: %w", err,
		)
	}

	// 3. Connect registry to Kind's Docker network.
	if err := connectRegistryToKind(o.registryName); err != nil {
		fmt.Println()
		return fmt.Errorf(
			"connecting registry to Kind network: %w", err,
		)
	}
	fmt.Printf(" done (%s)\n", formatElapsed(start))

	// 4. Set kubeconfig to the Kind cluster for the install step.
	fmt.Print("[2/3] Writing kubeconfig...")
	kubeconfig, err := provider.KubeConfig(o.clusterName, false)
	if err != nil {
		fmt.Println()
		return fmt.Errorf("getting kubeconfig: %w", err)
	}

	if o.output != "" {
		if err := os.WriteFile(
			o.output, []byte(kubeconfig), 0600,
		); err != nil {
			fmt.Println()
			return fmt.Errorf(
				"writing kubeconfig to %s: %w",
				o.output, err,
			)
		}
		kubeconfigPath = o.output
	} else {
		// Write to temp file for the install step.
		tmpFile, err := os.CreateTemp(
			"", "mgmt-kubeconfig-*.yaml",
		)
		if err != nil {
			fmt.Println()
			return fmt.Errorf(
				"creating temp kubeconfig: %w", err,
			)
		}
		defer os.Remove(tmpFile.Name())
		if _, err := tmpFile.WriteString(kubeconfig); err != nil {
			fmt.Println()
			return fmt.Errorf(
				"writing temp kubeconfig: %w", err,
			)
		}
		tmpFile.Close()
		kubeconfigPath = tmpFile.Name()
	}

	if o.output != "" {
		absPath, _ := filepath.Abs(o.output)
		fmt.Printf(" done (%s)\n", o.output)
		fmt.Printf(
			"      Use 'k9s --kubeconfig %s' to explore "+
				"the cluster.\n",
			absPath,
		)
	} else {
		fmt.Println(" done")
	}

	// 5. Run install (operator + provider client).
	fmt.Println("[3/3] Installing cleanroom operator...")
	if err := runProviderDeploy(
		cmd.Context(), &o.installOpts,
	); err != nil {
		return fmt.Errorf("installing operator: %w", err)
	}

	// 6. Wait for pods to be ready if requested.
	if o.wait {
		if err := waitForDevPods(
			cmd.Context(), o,
		); err != nil {
			return err
		}
	}

	fmt.Printf(
		"\nDev environment '%s' is ready.\n",
		o.clusterName,
	)
	if o.output != "" {
		absPath, _ := filepath.Abs(o.output)
		fmt.Printf(
			"Use 'k9s --kubeconfig %s' to explore "+
				"the cluster.\n",
			absPath,
		)
	}
	return nil
}

func runDevDown(ctx context.Context, o *devDownOpts) error {
	if err := checkPrereqs("docker"); err != nil {
		return err
	}

	// Save key bundles before deletion if requested.
	if o.save {
		fmt.Println("Saving key bundles for all " +
			"environments...")
		if err := devSaveAllEnvironments(ctx); err != nil {
			return fmt.Errorf(
				"saving key bundles: %w", err,
			)
		}
	}

	provider := newKindProvider()

	// 1. Delete Kind cluster.
	fmt.Printf("Deleting Kind cluster '%s'...\n", o.clusterName)
	if err := provider.Delete(o.clusterName, ""); err != nil {
		fmt.Printf(
			"Warning: deleting cluster: %v\n", err,
		)
	}

	fmt.Println("Dev environment torn down.")
	return nil
}

// waitForDevPods waits for operator and provider-client pods
// to reach Ready, with retry logic to handle pod restarts
// during rollout.
func waitForDevPods(
	ctx context.Context, o *devUpOpts,
) error {
	dur, err := parseTimeout(o.timeout)
	if err != nil {
		return err
	}

	ctx, cancel := context.WithTimeout(ctx, dur)
	defer cancel()

	ns := o.installOpts.namespace
	labels := []struct {
		label string
		name  string
	}{
		{"app=cleanroom-operator", "operator"},
		{"app=cluster-provider-client", "cluster provider client"},
		{"app=ccf-provider-client", "ccf provider client"},
	}

	if o.installOpts.enableTelemetry {
		labels = append(labels, struct {
			label string
			name  string
		}{"app=aspire-dashboard", "aspire dashboard"})
	}

	for _, l := range labels {
		fmt.Printf("Waiting for %s pods...\n", l.name)
		if err := waitForPodsReady(
			ctx, ns, l.label,
		); err != nil {
			return fmt.Errorf(
				"waiting for %s: %w", l.name, err,
			)
		}
		fmt.Printf("  %s pods are ready.\n", l.name)
	}

	return nil
}

// waitForPodsReady uses kubectl wait with short per-attempt
// timeouts to handle pods being deleted during rollouts.
func waitForPodsReady(
	ctx context.Context, namespace, label string,
) error {
	for {
		if err := ctx.Err(); err != nil {
			return fmt.Errorf("timed out")
		}

		c := exec.CommandContext(
			ctx, "kubectl", "wait",
			"--for=condition=Ready", "pods",
			"-l", label,
			"-n", namespace,
			"--kubeconfig", kubeconfigPath,
			"--timeout=15s",
		)
		c.Stdout = nil
		c.Stderr = nil
		if err := c.Run(); err == nil {
			return nil
		}

		fmt.Printf("  Retrying wait for %s...\n", label)
		select {
		case <-ctx.Done():
			return fmt.Errorf("timed out")
		case <-time.After(5 * time.Second):
		}
	}
}

func ensureKindCluster(
	provider *kindcluster.Provider, name string,
) error {
	clusters, err := provider.List()
	if err == nil {
		for _, c := range clusters {
			if c == name {
				return nil
			}
		}
	}

	return provider.Create(
		name,
		kindcluster.CreateWithV1Alpha4Config(
			kindClusterConfig(),
		),
		kindcluster.CreateWithDisplayUsage(false),
		kindcluster.CreateWithDisplaySalutation(false),
	)
}

func configureContainerdRegistry(
	provider *kindcluster.Provider,
	clusterName, registryName, registryPort string,
) error {
	nodes, err := provider.ListNodes(clusterName)
	if err != nil {
		return err
	}

	registryDir := fmt.Sprintf(
		"/etc/containerd/certs.d/localhost:%s", registryPort,
	)
	hostsToml := fmt.Sprintf(
		"[host.\"http://%s:%s\"]", registryName, registryPort,
	)

	for _, node := range nodes {
		nodeName := node.String()
		if err := runCmd(
			"docker", "exec", nodeName,
			"mkdir", "-p", registryDir,
		); err != nil {
			return fmt.Errorf(
				"creating registry dir on %s: %w",
				nodeName, err,
			)
		}

		// Pipe hostsToml content into the node.
		c := exec.Command(
			"docker", "exec", "-i", nodeName,
			"cp", "/dev/stdin",
			registryDir+"/hosts.toml",
		)
		c.Stdin = strings.NewReader(hostsToml)
		c.Stdout = os.Stdout
		c.Stderr = os.Stderr
		if err := c.Run(); err != nil {
			return fmt.Errorf(
				"writing hosts.toml on %s: %w",
				nodeName, err,
			)
		}
	}
	return nil
}

func connectRegistryToKind(registryName string) error {
	out, err := runCmdOutput(
		"docker", "inspect",
		"-f", "{{json .NetworkSettings.Networks.kind}}",
		registryName,
	)
	if err != nil {
		return nil // registry may not exist yet
	}
	if strings.TrimSpace(out) != "null" &&
		strings.TrimSpace(out) != "" {
		return nil // already connected
	}

	return runCmd(
		"docker", "network", "connect", "kind", registryName,
	)
}

func runCmd(name string, args ...string) error {
	c := exec.Command(name, args...)
	c.Stdout = os.Stdout
	c.Stderr = os.Stderr
	return c.Run()
}

func runCmdOutput(name string, args ...string) (string, error) {
	c := exec.Command(name, args...)
	var out bytes.Buffer
	c.Stdout = &out
	c.Stderr = os.Stderr
	err := c.Run()
	return out.String(), err
}

const (
	registryURLKey       = "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL"
	operatorImageKey     = "AZCLI_CLEANROOM_OPERATOR_IMAGE"
	providerClientImgKey = "AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE"
	ccfProviderImgKey    = "AZCLI_CCF_PROVIDER_CLIENT_IMAGE"
)

// applyEnvFileDefaults reads the env file and sets operator and
// provider-client image/tag from env vars when not already set
// via flags. Image env vars use "repo/image:tag" format.
func applyEnvFileDefaults(
	envFile string, opts *providerDeployOpts,
) error {
	raw, err := readRawEnvFile(envFile)
	if err != nil {
		return fmt.Errorf("reading env file: %w", err)
	}

	if opts.operatorImage == "" {
		if img, ok := raw[operatorImageKey]; ok {
			repo, tag := splitImageRef(img)
			opts.operatorImage = repo
			opts.operatorTag = tag
		} else if url, ok := raw[registryURLKey]; ok {
			opts.operatorImage = url + "/cleanroom-operator"
		}
	}

	if opts.providerImage == "" {
		if img, ok := raw[providerClientImgKey]; ok {
			repo, tag := splitImageRef(img)
			opts.providerImage = repo
			opts.providerTag = tag
		} else if url, ok := raw[registryURLKey]; ok {
			opts.providerImage = url +
				"/cleanroom-cluster/" +
				"cleanroom-cluster-provider-client"
		}
	}

	if opts.ccfProviderImage == "" {
		if img, ok := raw[ccfProviderImgKey]; ok {
			repo, tag := splitImageRef(img)
			opts.ccfProviderImage = repo
			opts.ccfProviderTag = tag
		}
	}

	// The local-idp image is not carried explicitly in the env
	// file. Derive it from the operator image's registry so it
	// lives in the same registry (and tag) as the operator. This
	// matters for non-local clusters (e.g. AKS) that cannot reach
	// the local dev registry the chart defaults to.
	if opts.localIdpImage == "" && opts.operatorImage != "" {
		registry := operatorImageRegistry(opts.operatorImage)
		if registry != "" {
			opts.localIdpImage = registry + "/local-idp"
			opts.localIdpTag = opts.operatorTag
		}
	}

	return nil
}

// operatorImageRegistry returns the registry prefix of the
// operator image repository (everything before the final
// "/cleanroom-operator" path segment). Returns "" if the
// expected suffix is not present.
func operatorImageRegistry(operatorImage string) string {
	const suffix = "/cleanroom-operator"
	if strings.HasSuffix(operatorImage, suffix) {
		return strings.TrimSuffix(operatorImage, suffix)
	}
	return ""
}

// splitImageRef splits "repo/image:tag" into ("repo/image", "tag").
// If no tag is present, returns ("image", "latest").
func splitImageRef(ref string) (string, string) {
	// Handle digest references (repo/image@sha256:...).
	if i := strings.LastIndex(ref, "@"); i >= 0 {
		return ref[:i], ref[i+1:]
	}
	if i := strings.LastIndex(ref, ":"); i >= 0 {
		// Avoid splitting on port numbers (e.g.
		// localhost:5000/image:tag). A tag never
		// contains "/".
		candidate := ref[i+1:]
		if !strings.Contains(candidate, "/") {
			return ref[:i], candidate
		}
	}
	return ref, "latest"
}

// readRawEnvFile parses a KEY=VALUE file without key remapping.
func readRawEnvFile(path string) (map[string]string, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	result := make(map[string]string)
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.SplitN(line, "=", 2)
		if len(parts) == 2 {
			result[parts[0]] = parts[1]
		}
	}
	return result, scanner.Err()
}
