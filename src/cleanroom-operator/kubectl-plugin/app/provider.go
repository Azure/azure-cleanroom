package app

import (
	"bufio"
	"context"
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/spf13/cobra"
	"helm.sh/helm/v3/pkg/action"
	"helm.sh/helm/v3/pkg/chart/loader"
	"helm.sh/helm/v3/pkg/cli"
	"helm.sh/helm/v3/pkg/release"
	"helm.sh/helm/v3/pkg/storage/driver"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"

	helmchart "github.com/Azure/azure-cleanroom/cleanroom-operator/helm"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/kubectl-plugin/prereqs"
)

const (
	releaseName = "cleanroom-operator"
)

type providerDeployOpts struct {
	clusterProviderEndpoint string
	ccfProviderEndpoint     string
	operatorImage           string
	operatorTag             string
	providerImage           string
	providerTag             string
	ccfProviderImage        string
	ccfProviderTag          string
	localIdpImage           string
	localIdpTag             string
	imagePullPolicy         string
	envFiles                []string
	credentialsProxy        bool
	enableTelemetry         bool
	namespace               string
	setValues               []string
	valuesFiles             []string

	// Component toggles for minimal installs (e.g. bootstrap).
	// Zero value keeps the component enabled (backwards
	// compatible with the full install path).
	operatorDisabled    bool
	ccfProviderDisabled bool
	localIdpDisabled    bool

	// providerVirtualDisabled turns off the virtual (Kind)
	// hostPath mounts (docker.sock, shared/workspace dirs) on
	// the provider clients. These are only valid when the
	// provider client runs on a local Kind host; on a real
	// workload cluster (e.g. AKS) they must be disabled. Zero
	// value keeps them enabled for dev up / bootstrap.
	providerVirtualDisabled bool

	// azureWorkloadIdentityClientId is the client ID of the
	// user-assigned managed identity federated with the
	// provider-client service accounts. When set, the cluster
	// and CCF provider clients authenticate to Azure ARM via
	// AKS Workload Identity instead of credentials-proxy. This
	// is the auth mechanism for self-contained AKS workload
	// clusters where no host az CLI creds are available.
	azureWorkloadIdentityClientId string

	// prereqsConfig is the name of a prepare-prereqs ConfigMap
	// to read the workload identity client ID from (key
	// "workloadIdentityClientId") when
	// azureWorkloadIdentityClientId is not set explicitly.
	prereqsConfig string

	// prereqsConfigNamespace is the namespace of the
	// prepare-prereqs ConfigMap. Defaults to the current
	// kubeconfig namespace (where prepare-prereqs writes it).
	prereqsConfigNamespace string
}

func newInstallCmd() *cobra.Command {
	o := &providerDeployOpts{}
	providerVirtual := true

	cmd := &cobra.Command{
		Use:   "install",
		Short: "Install the operator, CRDs, and provider client",
		Long: `Installs or upgrades the cleanroom-operator Helm chart,
which includes CRDs, the controller, and optionally the in-cluster
provider client.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			o.providerVirtualDisabled = !providerVirtual
			return runProviderDeploy(cmd.Context(), o)
		},
	}

	f := cmd.Flags()
	f.StringVar(&o.clusterProviderEndpoint,
		"cluster-provider-client-endpoint", "",
		"External cluster provider client endpoint "+
			"(disables in-cluster deployment)")
	f.StringVar(&o.ccfProviderEndpoint,
		"ccf-provider-client-endpoint", "",
		"External CCF provider client endpoint "+
			"(disables in-cluster deployment)")
	f.StringVar(&o.operatorImage,
		"operator-image", "",
		"Operator container image repository")
	f.StringVar(&o.operatorTag,
		"operator-tag", "latest",
		"Operator container image tag")
	f.StringVar(&o.providerImage,
		"cluster-provider-client-image", "",
		"Cluster provider client container image repository")
	f.StringVar(&o.providerTag,
		"cluster-provider-client-tag", "latest",
		"Cluster provider client container image tag")
	f.StringVar(&o.ccfProviderImage,
		"ccf-provider-client-image", "",
		"CCF provider client container image repository")
	f.StringVar(&o.ccfProviderTag,
		"ccf-provider-client-tag", "latest",
		"CCF provider client container image tag")
	f.StringSliceVar(&o.envFiles,
		"env-file", nil,
		"Path to KEY=VALUE env file for provider client config (repeatable)")
	f.StringVar(&o.imagePullPolicy,
		"image-pull-policy", "",
		"Image pull policy for operator and provider client (Always, IfNotPresent, Never)")
	f.BoolVar(&o.credentialsProxy,
		"credentials-proxy", false,
		"Enable credentials-proxy sidecar for Azure auth")
	f.StringVar(&o.azureWorkloadIdentityClientId,
		"azure-workload-identity-client-id", "",
		"Client ID of the user-assigned managed identity "+
			"federated with the operator and provider-client "+
			"service accounts (cleanroom-operator, "+
			"cluster-provider-client, ccf-provider-client). "+
			"Enables AKS Workload Identity auth to Azure. Use "+
			"on self-contained AKS workload clusters instead "+
			"of --credentials-proxy.")
	f.StringVar(&o.prereqsConfig,
		"prereqs-config", "",
		"Name of a prepare-prereqs ConfigMap to read the "+
			"workload identity client ID from when "+
			"--azure-workload-identity-client-id is not set.")
	f.StringVar(&o.prereqsConfigNamespace,
		"prereqs-config-namespace",
		defaultNamespaceFromKubeconfig(),
		"Namespace of the --prereqs-config ConfigMap "+
			"(defaults to the current kubeconfig namespace).")
	f.BoolVar(&o.enableTelemetry,
		"enable-telemetry", true,
		"Enable OTLP tracing with aspire-dashboard")
	f.BoolVar(&providerVirtual,
		"provider-virtual", true,
		"Enable virtual (Kind) hostPath mounts on the provider "+
			"clients (docker.sock, shared dirs). Set to false "+
			"when installing on a real workload cluster such as "+
			"AKS, which has no host Docker socket.")
	f.StringVar(&o.namespace,
		"namespace", operatorNamespace,
		"Kubernetes namespace")
	f.StringSliceVar(&o.setValues,
		"set", nil,
		"Set Helm values (key=value, repeatable)")
	f.StringSliceVar(&o.valuesFiles,
		"values", nil,
		"Path to YAML file with Helm value overrides")
	return cmd
}

func runProviderDeploy(
	ctx context.Context,
	o *providerDeployOpts,
) error {
	// Derive operator, provider-client, and local-idp image
	// references from the env files. This is idempotent (only
	// fills values that are still empty), so it is safe even
	// when the caller — e.g. 'dev up' or 'bootstrap start' —
	// has already applied them. The standalone 'install'
	// command relies on this to resolve images (including
	// local-idp) from the env-file registry rather than the
	// chart defaults, which point at the local dev registry.
	for _, ef := range o.envFiles {
		if err := applyEnvFileDefaults(ef, o); err != nil {
			return err
		}
	}

	// Resolve the workload identity client ID from a
	// prepare-prereqs ConfigMap when not set explicitly.
	if o.azureWorkloadIdentityClientId == "" &&
		o.prereqsConfig != "" {
		clientID, err := readPrereqsWorkloadIdentityClientID(
			ctx, o.prereqsConfig, o.prereqsConfigNamespace,
		)
		if err != nil {
			return err
		}
		if clientID != "" {
			o.azureWorkloadIdentityClientId = clientID
			fmt.Printf(
				"Using workload identity client ID %s "+
					"from prereqs ConfigMap %q\n",
				clientID, o.prereqsConfig,
			)
		}
	}

	settings := cli.New()
	if kubeconfigPath != "" {
		settings.KubeConfig = kubeconfigPath
	}
	settings.SetNamespace(o.namespace)

	actionConfig := new(action.Configuration)
	if err := actionConfig.Init(
		settings.RESTClientGetter(),
		o.namespace,
		"secret",
		func(format string, v ...interface{}) {
			fmt.Printf(format+"\n", v...)
		},
	); err != nil {
		return fmt.Errorf("initializing Helm: %w", err)
	}

	// Load chart from embedded FS.
	chartPath, err := writeEmbeddedChart()
	if err != nil {
		return fmt.Errorf("loading embedded chart: %w", err)
	}
	defer os.RemoveAll(chartPath)

	chart, err := loader.Load(chartPath)
	if err != nil {
		return fmt.Errorf("loading chart: %w", err)
	}

	vals := map[string]interface{}{
		"namespace": o.namespace,
	}

	setNestedValue(
		vals, !o.operatorDisabled,
		"operator", "enabled",
	)

	if o.operatorImage != "" {
		setNestedValue(
			vals, o.operatorImage,
			"operator", "image", "repository",
		)
	}
	setNestedValue(
		vals, o.operatorTag,
		"operator", "image", "tag",
	)

	if o.imagePullPolicy != "" {
		setNestedValue(
			vals, o.imagePullPolicy,
			"operator", "image", "pullPolicy",
		)
	}

	if o.clusterProviderEndpoint != "" {
		setNestedValue(vals, false, "providerClient", "enabled")
		setNestedValue(
			vals, o.clusterProviderEndpoint,
			"providerClient", "endpoint",
		)
	} else {
		setNestedValue(vals, true, "providerClient", "enabled")
		if o.providerImage != "" {
			setNestedValue(
				vals, o.providerImage,
				"providerClient", "image", "repository",
			)
		}
		setNestedValue(
			vals, o.providerTag,
			"providerClient", "image", "tag",
		)
		if o.imagePullPolicy != "" {
			setNestedValue(
				vals, o.imagePullPolicy,
				"providerClient", "image", "pullPolicy",
			)
		}
	}

	if o.ccfProviderEndpoint != "" {
		setNestedValue(
			vals, false, "ccfProviderClient", "enabled",
		)
		setNestedValue(
			vals, o.ccfProviderEndpoint,
			"ccfProviderClient", "endpoint",
		)
	} else {
		setNestedValue(
			vals, true, "ccfProviderClient", "enabled",
		)
		if o.ccfProviderImage != "" {
			setNestedValue(
				vals, o.ccfProviderImage,
				"ccfProviderClient", "image", "repository",
			)
		}
		setNestedValue(
			vals, o.ccfProviderTag,
			"ccfProviderClient", "image", "tag",
		)
		if o.imagePullPolicy != "" {
			setNestedValue(
				vals, o.imagePullPolicy,
				"ccfProviderClient", "image", "pullPolicy",
			)
		}
	}

	// Explicit disable overrides (e.g. bootstrap cluster).
	if o.ccfProviderDisabled {
		setNestedValue(
			vals, false, "ccfProviderClient", "enabled",
		)
	}

	// Disable the virtual (Kind) hostPath mounts on both
	// provider clients when installing on a real workload
	// cluster (e.g. AKS) that has no host Docker socket.
	if o.providerVirtualDisabled {
		setNestedValue(
			vals, false, "providerClient", "virtual", "enabled",
		)
		setNestedValue(
			vals, false,
			"ccfProviderClient", "virtual", "enabled",
		)
	}

	if o.credentialsProxy {
		setNestedValue(
			vals, true,
			"credentialsProxy", "enabled",
		)
		homeDir, _ := os.UserHomeDir()
		setNestedValue(
			vals, homeDir+"/.azure",
			"credentialsProxy", "azureDir",
		)
	}

	// AKS Workload Identity: federate the provider-client
	// service accounts with a user-assigned managed identity so
	// they authenticate to Azure ARM without host az CLI creds.
	if o.azureWorkloadIdentityClientId != "" {
		setNestedValue(
			vals, o.azureWorkloadIdentityClientId,
			"operator", "azure", "clientId",
		)
		setNestedValue(
			vals, o.azureWorkloadIdentityClientId,
			"providerClient", "azure", "clientId",
		)
		setNestedValue(
			vals, o.azureWorkloadIdentityClientId,
			"ccfProviderClient", "azure", "clientId",
		)
	}

	if o.enableTelemetry {
		setNestedValue(vals, true, "telemetry", "enabled")
		setNestedValue(
			vals,
			"http://aspire-dashboard:18889",
			"telemetry", "otlpEndpoint",
		)
	}

	// Parse env files and populate providerClient.env,
	// ccfProviderClient.env, and operator.env based on key
	// prefixes.
	for _, ef := range o.envFiles {
		clusterEnv, ccfEnv, operatorEnv, err := parseEnvFiles(ef)
		if err != nil {
			return fmt.Errorf("parsing env file %s: %w", ef, err)
		}
		if len(clusterEnv) > 0 {
			existing, _ := getNestedMap(
				vals, "providerClient", "env",
			)
			for k, v := range clusterEnv {
				existing[k] = v
			}
			setNestedValue(
				vals, existing, "providerClient", "env",
			)
		}
		if len(ccfEnv) > 0 {
			existing, _ := getNestedMap(
				vals, "ccfProviderClient", "env",
			)
			for k, v := range ccfEnv {
				existing[k] = v
			}
			setNestedValue(
				vals, existing, "ccfProviderClient", "env",
			)
		}
		if len(operatorEnv) > 0 {
			existing, _ := getNestedMap(
				vals, "operator", "env",
			)
			for k, v := range operatorEnv {
				existing[k] = v
			}
			setNestedValue(
				vals, existing, "operator", "env",
			)
		}
	}

	// Parse --set flags.
	for _, s := range o.setValues {
		parts := strings.SplitN(s, "=", 2)
		if len(parts) == 2 {
			keys := strings.Split(parts[0], ".")
			setNestedValue(vals, parts[1], keys...)
		}
	}

	// Determine total steps for progress display.
	step := 0
	totalSteps := 1 // cleanroom-operator chart
	if !o.localIdpDisabled {
		totalSteps++ // local-idp
	}
	if o.enableTelemetry {
		totalSteps++
	}
	// Deploy aspire-dashboard first if telemetry is enabled so
	// its endpoint is up when operator/provider-client pods start.
	if o.enableTelemetry {
		step++
		fmt.Printf(
			"  [%d/%d] Installing aspire-dashboard...",
			step, totalSteps,
		)
		start := time.Now()
		if err := deployAspireDashboard(
			ctx, settings, o.namespace,
		); err != nil {
			fmt.Println()
			return fmt.Errorf(
				"deploying aspire-dashboard: %w", err,
			)
		}
		fmt.Printf(" done (%s)\n", formatElapsed(start))
	}

	// Deploy local-idp for user authentication via JWT.
	if !o.localIdpDisabled {
		step++
		fmt.Printf(
			"  [%d/%d] Installing local-idp...", step, totalSteps,
		)
		start := time.Now()
		if err := deployLocalIdp(
			ctx, settings, o.namespace,
			o.localIdpImage, o.localIdpTag,
			o.imagePullPolicy,
		); err != nil {
			fmt.Println()
			return fmt.Errorf(
				"deploying local-idp: %w", err,
			)
		}
		fmt.Printf(" done (%s)\n", formatElapsed(start))
	}

	// Check if release exists (upgrade vs install).
	doInstall, err := shouldInstall(
		actionConfig, releaseName,
	)
	if err != nil {
		return err
	}

	step++
	verb := "Installing"
	if !doInstall {
		verb = "Upgrading"
	}
	fmt.Printf(
		"  [%d/%d] %s cleanroom-operator...",
		step, totalSteps, verb,
	)
	start := time.Now()

	if doInstall {
		install := action.NewInstall(actionConfig)
		install.ReleaseName = releaseName
		install.Namespace = o.namespace
		install.CreateNamespace = true
		install.Wait = true
		install.Timeout = 10 * time.Minute

		if _, err := install.RunWithContext(
			ctx, chart, vals,
		); err != nil {
			fmt.Println()
			return fmt.Errorf("installing chart: %w", err)
		}
	} else {
		upgrade := action.NewUpgrade(actionConfig)
		upgrade.Namespace = o.namespace
		upgrade.Wait = true
		upgrade.Timeout = 10 * time.Minute

		if _, err := upgrade.RunWithContext(
			ctx, releaseName, chart, vals,
		); err != nil {
			fmt.Println()
			return fmt.Errorf("upgrading chart: %w", err)
		}
	}

	fmt.Printf(" done (%s)\n", formatElapsed(start))
	return nil
}

func deployAspireDashboard(
	ctx context.Context,
	settings *cli.EnvSettings,
	namespace string,
) error {
	actionConfig := new(action.Configuration)
	if err := actionConfig.Init(
		settings.RESTClientGetter(),
		namespace,
		"secret",
		func(format string, v ...interface{}) {
			fmt.Printf(format+"\n", v...)
		},
	); err != nil {
		return fmt.Errorf("initializing Helm: %w", err)
	}

	chartPath, err := writeEmbeddedAspireChart()
	if err != nil {
		return fmt.Errorf(
			"loading aspire-dashboard chart: %w", err,
		)
	}
	defer os.RemoveAll(chartPath)

	chart, err := loader.Load(chartPath)
	if err != nil {
		return fmt.Errorf("loading chart: %w", err)
	}

	vals := map[string]interface{}{
		"namespace": namespace,
	}

	const aspireName = "aspire-dashboard"

	doInstall, err := shouldInstall(actionConfig, aspireName)
	if err != nil {
		return err
	}

	if doInstall {
		install := action.NewInstall(actionConfig)
		install.ReleaseName = aspireName
		install.Namespace = namespace
		install.CreateNamespace = true
		install.Wait = true
		install.Timeout = 5 * time.Minute

		if _, err := install.RunWithContext(
			ctx, chart, vals,
		); err != nil {
			return fmt.Errorf(
				"installing aspire-dashboard: %w", err,
			)
		}
	} else {
		upgrade := action.NewUpgrade(actionConfig)
		upgrade.Namespace = namespace
		upgrade.Wait = true
		upgrade.Timeout = 5 * time.Minute

		if _, err := upgrade.RunWithContext(
			ctx, aspireName, chart, vals,
		); err != nil {
			return fmt.Errorf(
				"upgrading aspire-dashboard: %w", err,
			)
		}
	}

	return nil
}

func writeEmbeddedChart() (string, error) {
	tmpDir, err := os.MkdirTemp("", "cleanroom-chart-*")
	if err != nil {
		return "", err
	}

	err = helmchart.CopyChartToDir(tmpDir)
	if err != nil {
		os.RemoveAll(tmpDir)
		return "", err
	}
	return tmpDir, nil
}

func writeEmbeddedAspireChart() (string, error) {
	tmpDir, err := os.MkdirTemp("", "aspire-dashboard-chart-*")
	if err != nil {
		return "", err
	}

	err = helmchart.CopyAspireDashboardChartToDir(tmpDir)
	if err != nil {
		os.RemoveAll(tmpDir)
		return "", err
	}
	return tmpDir, nil
}

func deployLocalIdp(
	ctx context.Context,
	settings *cli.EnvSettings,
	namespace string,
	image string,
	tag string,
	pullPolicy string,
) error {
	actionConfig := new(action.Configuration)
	if err := actionConfig.Init(
		settings.RESTClientGetter(),
		namespace,
		"secret",
		func(format string, v ...interface{}) {
			fmt.Printf(format+"\n", v...)
		},
	); err != nil {
		return fmt.Errorf("initializing Helm: %w", err)
	}

	chartPath, err := writeEmbeddedLocalIdpChart()
	if err != nil {
		return fmt.Errorf(
			"loading local-idp chart: %w", err,
		)
	}
	defer os.RemoveAll(chartPath)

	chart, err := loader.Load(chartPath)
	if err != nil {
		return fmt.Errorf("loading chart: %w", err)
	}

	vals := map[string]interface{}{
		"namespace": namespace,
	}

	// Override the image so it comes from the same registry as
	// the rest of the deployment (derived from --env-file). The
	// chart default points at the local dev registry, which is
	// unreachable from non-local clusters such as AKS.
	if image != "" {
		setNestedValue(vals, image, "image", "repository")
	}
	if tag != "" {
		setNestedValue(vals, tag, "image", "tag")
	}
	if pullPolicy != "" {
		setNestedValue(vals, pullPolicy, "image", "pullPolicy")
	}

	// Check if a restored IDP keys Secret exists. If so,
	// pass it to the Helm chart so the local-IDP loads
	// the pre-existing keys on startup.
	idpKeysSecret, err := findIdpKeysSecret(
		ctx, namespace,
	)
	if err == nil && idpKeysSecret != "" {
		vals["existingKeysSecret"] = idpKeysSecret
	}

	const localIdpName = "local-idp"

	doInstall, err := shouldInstall(
		actionConfig, localIdpName,
	)
	if err != nil {
		return err
	}

	if doInstall {
		install := action.NewInstall(actionConfig)
		install.ReleaseName = localIdpName
		install.Namespace = namespace
		install.CreateNamespace = true
		install.Wait = true
		install.Timeout = 5 * time.Minute

		if _, err := install.RunWithContext(
			ctx, chart, vals,
		); err != nil {
			return fmt.Errorf(
				"installing local-idp: %w", err,
			)
		}
	} else {
		upgrade := action.NewUpgrade(actionConfig)
		upgrade.Namespace = namespace
		upgrade.Wait = true
		upgrade.Timeout = 5 * time.Minute

		if _, err := upgrade.RunWithContext(
			ctx, localIdpName, chart, vals,
		); err != nil {
			return fmt.Errorf(
				"upgrading local-idp: %w", err,
			)
		}
	}

	return nil
}

func writeEmbeddedLocalIdpChart() (string, error) {
	tmpDir, err := os.MkdirTemp(
		"", "local-idp-chart-*",
	)
	if err != nil {
		return "", err
	}

	err = helmchart.CopyLocalIdpChartToDir(tmpDir)
	if err != nil {
		os.RemoveAll(tmpDir)
		return "", err
	}
	return tmpDir, nil
}

// findIdpKeysSecret looks for a restored IDP keys Secret
// in the given namespace. It searches for Secrets whose
// name ends with "-idp-keys".
func findIdpKeysSecret(
	ctx context.Context,
	namespace string,
) (string, error) {
	clientset, err := getClientset()
	if err != nil {
		return "", err
	}

	secrets, err := clientset.CoreV1().Secrets(
		namespace,
	).List(ctx, metav1.ListOptions{})
	if err != nil {
		return "", err
	}

	for _, s := range secrets.Items {
		if strings.HasSuffix(
			s.Name, idpKeysSecretSuffix,
		) {
			return s.Name, nil
		}
	}
	return "", fmt.Errorf("no IDP keys Secret found")
}

func parseEnvFile(path string) (map[string]interface{}, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()

	result := make(map[string]interface{})
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.SplitN(line, "=", 2)
		if len(parts) == 2 {
			key := parts[0]
			if strings.HasPrefix(key, "AZCLI_CLEANROOM_") {
				key = "CR_" + strings.TrimPrefix(key, "AZCLI_CLEANROOM_")
			}
			result[key] = parts[1]
		}
	}
	return result, scanner.Err()
}

// parseEnvFiles reads a KEY=VALUE file and dispatches keys into
// cluster-provider, ccf-provider, and operator maps based on
// prefix:
//   - AZCLI_CGS_* → strip AZCLI_ → operatorEnv
//   - AZCLI_CCF_PROVIDER_* → strip AZCLI_ → CCF_PROVIDER_* → ccfEnv
//   - AZCLI_CLEANROOM_* → strip prefix → CR_* → clusterEnv
//   - Other keys → clusterEnv unchanged
//
// azcliCleanroomEnvMap maps AZCLI_CLEANROOM_* env var keys
// (that don't follow the AZCLI_CLEANROOM_CLUSTER_PROVIDER_*
// pattern) to the CR_CLUSTER_PROVIDER_* names expected by
// ImageUtils.cs. This must match the explicit mappings in
// docker-compose.yaml.
var azcliCleanroomEnvMap = map[string]string{
	"AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL": "" +
		"CR_CLUSTER_PROVIDER_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL",
	"AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL": "" +
		"CR_CLUSTER_PROVIDER_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL",
	"AZCLI_CLEANROOM_CVM_MEASUREMENTS_VIRTUAL_DOCUMENT_URL": "" +
		"CR_CLUSTER_PROVIDER_CLEANROOM_CVM_MEASUREMENTS_VIRTUAL_DOCUMENT_URL",
	"AZCLI_CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL": "" +
		"CR_CLUSTER_PROVIDER_CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL",
	"AZCLI_CLEANROOM_INFERENCING_DIGESTS_DOCUMENT_URL": "" +
		"CR_CLUSTER_PROVIDER_INFERENCING_DIGESTS_DOCUMENT_URL",
	"AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL": "" +
		"CR_CLUSTER_PROVIDER_CLEANROOM_ANALYTICS_IMAGE_URL",
	"AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL": "" +
		"CR_CLUSTER_PROVIDER_CLEANROOM_ANALYTICS_IMAGE_POLICY_DOCUMENT_URL",
	"AZCLI_CLEANROOM_KARPENTER_PROVIDER_IMAGE": "" +
		"CR_CLUSTER_PROVIDER_KARPENTER_PROVIDER_IMAGE",
	"AZCLI_CLEANROOM_KARPENTER_PROVIDER_CHART_URL": "" +
		"CR_CLUSTER_PROVIDER_KARPENTER_PROVIDER_CHART_URL",
}

func parseEnvFiles(
	path string,
) (clusterEnv, ccfEnv, operatorEnv map[string]interface{}, err error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, nil, nil, err
	}
	defer f.Close()

	clusterEnv = make(map[string]interface{})
	ccfEnv = make(map[string]interface{})
	operatorEnv = make(map[string]interface{})
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		parts := strings.SplitN(line, "=", 2)
		if len(parts) != 2 {
			continue
		}
		key := parts[0]
		value := parts[1]
		switch {
		case strings.HasPrefix(key, "AZCLI_CGS_"):
			// Strip AZCLI_ prefix: operator expects
			// CGS_* env vars (e.g. CGS_CLIENT_IMAGE).
			mapped := strings.TrimPrefix(key, "AZCLI_")
			operatorEnv[mapped] = value
		case strings.HasPrefix(key, "AZCLI_CCF_PROVIDER_"):
			// Strip AZCLI_ prefix: container expects
			// CCF_PROVIDER_* env vars.
			mapped := strings.TrimPrefix(key, "AZCLI_")
			ccfEnv[mapped] = value
		case strings.HasPrefix(key, "AZCLI_CLEANROOM_"):
			mapped, ok := azcliCleanroomEnvMap[key]
			if !ok {
				mapped = "CR_" + strings.TrimPrefix(
					key, "AZCLI_CLEANROOM_",
				)
			}
			clusterEnv[mapped] = value
		default:
			clusterEnv[key] = value
		}
	}
	return clusterEnv, ccfEnv, operatorEnv, scanner.Err()
}

// getNestedMap retrieves or creates a nested map[string]interface{}
// within vals at the given key path.
func getNestedMap(
	vals map[string]interface{}, keys ...string,
) (map[string]interface{}, bool) {
	m := vals
	for _, key := range keys {
		next, ok := m[key].(map[string]interface{})
		if !ok {
			next = make(map[string]interface{})
			m[key] = next
			return next, false
		}
		m = next
	}
	return m, true
}

// readPrereqsWorkloadIdentityClientID reads the workload
// identity client ID recorded by 'prepare-prereqs
// --setup-workload-identity' from the given ConfigMap.
func readPrereqsWorkloadIdentityClientID(
	ctx context.Context,
	name string,
	namespace string,
) (string, error) {
	if namespace == "" {
		namespace = "default"
	}
	clientset, err := getClientset()
	if err != nil {
		return "", fmt.Errorf(
			"creating Kubernetes client: %w", err,
		)
	}
	cm, err := clientset.CoreV1().ConfigMaps(namespace).Get(
		ctx, name, metav1.GetOptions{},
	)
	if err != nil {
		return "", fmt.Errorf(
			"reading prereqs ConfigMap %q in namespace "+
				"%q: %w", name, namespace, err,
		)
	}
	return cm.Data[prereqs.WorkloadIdentityClientIDKey], nil
}

func newUninstallCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "uninstall",
		Short: "Uninstall the operator, CRDs, and provider client",
		Long: `Uninstalls the cleanroom-operator and aspire-dashboard
Helm releases from the cluster.`,
		RunE: func(cmd *cobra.Command, args []string) error {
			return runUninstall(cmd.Context(), namespace)
		},
	}

	cmd.Flags().StringVar(
		&namespace, "namespace", operatorNamespace,
		"Kubernetes namespace",
	)
	return cmd
}

func runUninstall(
	ctx context.Context,
	namespace string,
) error {
	settings := cli.New()
	if kubeconfigPath != "" {
		settings.KubeConfig = kubeconfigPath
	}
	settings.SetNamespace(namespace)

	actionConfig := new(action.Configuration)
	if err := actionConfig.Init(
		settings.RESTClientGetter(),
		namespace,
		"secret",
		func(format string, v ...interface{}) {
			fmt.Printf(format+"\n", v...)
		},
	); err != nil {
		return fmt.Errorf("initializing Helm: %w", err)
	}

	for _, name := range []string{
		releaseName, "aspire-dashboard",
	} {
		uninstall := action.NewUninstall(actionConfig)
		uninstall.Wait = true
		uninstall.Timeout = 5 * time.Minute
		fmt.Printf(
			"Uninstalling %s from namespace %s...\n",
			name, namespace,
		)
		if _, err := uninstall.Run(name); err != nil {
			fmt.Printf(
				"Warning: uninstalling %s: %v\n", name, err,
			)
		}
	}

	fmt.Println("Uninstall complete.")
	return nil
}

// recoverStuckRelease detects a Helm release stuck in a pending
// state (pending-install, pending-upgrade, pending-rollback) and
// recovers it so the next install/upgrade can proceed. Returns
// true if the release was uninstalled (caller should do a fresh
// install).
func recoverStuckRelease(
	cfg *action.Configuration,
	name string,
	releases []*release.Release,
) (bool, error) {
	if len(releases) == 0 {
		return false, nil
	}
	latest := releases[len(releases)-1]
	status := latest.Info.Status

	switch status {
	case release.StatusPendingInstall:
		fmt.Printf(
			"Release %s is stuck in %s, uninstalling...\n",
			name, status,
		)
		uninstall := action.NewUninstall(cfg)
		if _, err := uninstall.Run(name); err != nil {
			return false, fmt.Errorf(
				"cleaning up stuck release %s: %w", name, err,
			)
		}
		return true, nil
	case release.StatusPendingUpgrade,
		release.StatusPendingRollback:
		fmt.Printf(
			"Release %s is stuck in %s, rolling back...\n",
			name, status,
		)
		rollback := action.NewRollback(cfg)
		rollback.Force = true
		if err := rollback.Run(name); err != nil {
			return false, fmt.Errorf(
				"rolling back stuck release %s: %w", name, err,
			)
		}
	case release.StatusFailed:
		fmt.Printf(
			"Release %s is in %s state, will upgrade...\n",
			name, status,
		)
	}

	return false, nil
}

// shouldInstall checks Helm history for a release and recovers
// stuck releases. Returns true if a fresh install is needed.
func shouldInstall(
	cfg *action.Configuration,
	name string,
) (bool, error) {
	histClient := action.NewHistory(cfg)
	histClient.Max = 1
	releases, err := histClient.Run(name)

	if err == driver.ErrReleaseNotFound {
		return true, nil
	}

	uninstalled, err := recoverStuckRelease(
		cfg, name, releases,
	)
	if err != nil {
		return false, err
	}
	return uninstalled, nil
}

// formatElapsed returns a human-friendly elapsed time string
// like "5s", "1m30s", or "2m0s".
func formatElapsed(start time.Time) string {
	d := time.Since(start).Round(time.Second)
	if d < time.Minute {
		return fmt.Sprintf("%ds", int(d.Seconds()))
	}
	m := int(d.Minutes())
	s := int(d.Seconds()) % 60
	return fmt.Sprintf("%dm%ds", m, s)
}

func setNestedValue(
	m map[string]interface{},
	value interface{},
	keys ...string,
) {
	for i, key := range keys {
		if i == len(keys)-1 {
			m[key] = value
			return
		}
		next, ok := m[key].(map[string]interface{})
		if !ok {
			next = make(map[string]interface{})
			m[key] = next
		}
		m = next
	}
}
