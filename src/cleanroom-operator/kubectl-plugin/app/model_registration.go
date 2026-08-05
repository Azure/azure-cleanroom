package app

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
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

	"github.com/Azure/azure-cleanroom/cleanroom-operator/internal/azure"
)

var modelRegistrationGVR = schema.GroupVersionResource{
	Group:    "cleanroom.azure.com",
	Version:  "v1alpha1",
	Resource: "modelregistrations",
}

func newModelRegistrationCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:     "model-registration",
		Aliases: []string{"mr"},
		Short:   "Manage model deployments",
	}

	cmd.AddCommand(newModelRegistrationCreateCmd())
	cmd.AddCommand(newModelRegistrationGetCmd())
	cmd.AddCommand(newModelRegistrationListCmd())
	cmd.AddCommand(newModelRegistrationDeleteCmd())
	cmd.AddCommand(newModelRegistrationStatusCmd())
	cmd.AddCommand(newModelRegistrationWaitCmd())
	cmd.AddCommand(newModelRegistrationReconcileCmd())
	return cmd
}

type mrCreateOpts struct {
	namespace         string
	env               string
	modelID           string
	modelPath         string
	blobPrefix        string
	sourceFile        string
	storageAccountId  string
	managedIdentityId string
	containerName     string
	encryptionMode    string
	uploadMode        string
	vmSize            string
	resourceGroup     string
	location          string
	autoDeploy        bool
	noWait            bool
	skipModelCheck    bool
	timeout           string
}

func newModelRegistrationCreateCmd() *cobra.Command {
	o := &mrCreateOpts{}

	cmd := &cobra.Command{
		Use:   "create <name>",
		Short: "Create a model deployment with governance",
		Long: `Create a ModelRegistration custom resource.
This sets up flex nodes, registers datastores,
publishes datasets, and creates governance documents.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelCreate(
				cmd.Context(), args[0], o,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(&o.namespace, "namespace", "n",
		"", "Kubernetes namespace "+
			"(defaults to current context)")
	f.StringVar(&o.env, "env", "",
		"Name of the Environment resource (required)")
	f.StringVar(&o.modelID, "model-id", "",
		"Model identifier (required)")
	f.StringVar(&o.modelPath, "model-path", "",
		"Blob path to the model within the storage "+
			"container (e.g. models/tinyllama/model.gguf)")
	f.StringVar(&o.blobPrefix, "blob-prefix", "",
		"Override blob prefix for model upload "+
			"(default: derived from model ID)")
	f.StringVar(&o.sourceFile, "source-file", "",
		"Specific file to upload from HuggingFace "+
			"repo (required for multi-GGUF repos)")
	f.StringVar(&o.storageAccountId,
		"storage-account-id", "",
		"ARM resource ID of the storage account")
	f.StringVar(&o.managedIdentityId,
		"managed-identity-id", "",
		"ARM resource ID of the managed identity")
	f.StringVar(&o.containerName,
		"container-name", "models",
		"Blob container name for model storage")
	f.StringVar(&o.encryptionMode,
		"encryption-mode", "SSE",
		"Encryption mode (SSE or CSE)")
	f.StringVar(&o.uploadMode,
		"upload-mode", "auto",
		"Upload mode: auto or skip")
	f.StringVar(&o.vmSize, "vm-size", "",
		"VM size for the flex node")
	f.StringVar(&o.resourceGroup,
		"resource-group", "",
		"Resource group for auto-created storage "+
			"and identity resources (simple mode)")
	f.StringVar(&o.location, "location", "",
		"Azure location for auto-created resources "+
			"(simple mode)")
	f.BoolVar(&o.noWait, "no-wait", false,
		"Do not wait for the model to be ready")
	f.BoolVar(&o.autoDeploy, "auto-deploy", false,
		"Automatically create a "+
			"ModelDeployment when ready")
	f.BoolVar(&o.skipModelCheck,
		"skip-model-check", false,
		"Skip HuggingFace model existence check")
	f.StringVar(&o.timeout, "timeout", "10m",
		"Timeout for waiting")

	_ = cmd.MarkFlagRequired("env")
	_ = cmd.MarkFlagRequired("model-id")

	return cmd
}

func runModelCreate(
	ctx context.Context,
	name string,
	o *mrCreateOpts,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if o.namespace == "" {
		o.namespace = defaultNamespaceFromKubeconfig()
	}

	if !o.skipModelCheck {
		fmt.Printf(
			"Checking model %q on HuggingFace...\n",
			o.modelID,
		)
		_, err = azure.FetchHFModelInfo(
			ctx, o.modelID,
		)
		if err != nil {
			fmt.Fprintf(os.Stderr,
				"\u26a0 Warning: model %q not found "+
					"on HuggingFace (may be private "+
					"or misspelled): %v\n",
				o.modelID, err,
			)
			fmt.Print("Continue? [Y/n] ")
			reader := bufio.NewReader(os.Stdin)
			answer, _ := reader.ReadString('\n')
			answer = strings.TrimSpace(
				strings.ToLower(answer),
			)
			if answer == "n" || answer == "no" {
				return fmt.Errorf("aborted by user")
			}
		}
	}

	// Simple mode: auto-create storage account and
	// managed identity if not provided.
	if o.storageAccountId == "" ||
		o.managedIdentityId == "" {
		if err := ensureModelInfra(o); err != nil {
			return fmt.Errorf(
				"setting up model infrastructure: %w",
				err,
			)
		}
	}

	model := map[string]interface{}{
		"id": o.modelID,
	}
	if o.modelPath != "" {
		model["path"] = o.modelPath
	}
	if o.blobPrefix != "" {
		model["blobPrefix"] = o.blobPrefix
	}
	if o.sourceFile != "" {
		model["sourceFile"] = o.sourceFile
	}

	spec := map[string]interface{}{
		"environmentRef": o.env,
		"model":          model,
		"storage": map[string]interface{}{
			"containerName":  o.containerName,
			"encryptionMode": o.encryptionMode,
		},
		"upload": map[string]interface{}{
			"mode": o.uploadMode,
		},
	}

	if o.storageAccountId != "" {
		storage :=
			spec["storage"].(map[string]interface{})
		storage["storageAccountId"] = o.storageAccountId
	}

	if o.managedIdentityId != "" {
		storage :=
			spec["storage"].(map[string]interface{})
		storage["managedIdentityId"] =
			o.managedIdentityId
	}

	if o.vmSize != "" {
		spec["vmSize"] = o.vmSize
	}

	if o.autoDeploy {
		spec["autoDeploy"] = true
	}

	obj := &unstructured.Unstructured{
		Object: map[string]interface{}{
			"apiVersion": "cleanroom.azure.com/v1alpha1",
			"kind":       "ModelRegistration",
			"metadata": map[string]interface{}{
				"name":      name,
				"namespace": o.namespace,
			},
			"spec": spec,
		},
	}

	result, err := client.Resource(modelRegistrationGVR).
		Namespace(o.namespace).
		Apply(ctx, name, obj, metav1.ApplyOptions{
			FieldManager: "kubectl-cleanroom",
		})
	if err != nil {
		return fmt.Errorf(
			"applying ModelRegistration: %w", err,
		)
	}

	fmt.Printf(
		"ModelRegistration %q created in namespace %q\n",
		result.GetName(), result.GetNamespace(),
	)

	if !o.noWait {
		return runModelWait(
			ctx, name, o.namespace, "Ready",
			o.timeout,
		)
	}

	return nil
}

func newModelRegistrationGetCmd() *cobra.Command {
	var namespace string
	var outputFormat string

	cmd := &cobra.Command{
		Use:   "get <name>",
		Short: "Get a ModelRegistration resource",
		Args:  cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelGet(
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

func runModelGet(
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

	result, err := client.Resource(modelRegistrationGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting ModelRegistration: %w", err,
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
	modelId, _, _ := unstructured.NestedString(
		result.Object,
		"spec", "model", "id",
	)
	envRef, _, _ := unstructured.NestedString(
		result.Object,
		"spec", "environmentRef",
	)

	w := tabwriter.NewWriter(
		os.Stdout, 0, 4, 2, ' ', 0,
	)
	fmt.Fprintln(w, "NAME\tMODEL\tENVIRONMENT\tPHASE")
	fmt.Fprintf(w, "%s\t%s\t%s\t%s\n",
		result.GetName(), modelId, envRef, phase)
	w.Flush()

	return nil
}

func newModelRegistrationListCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "list",
		Short: "List ModelRegistration resources",
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelList(
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

func runModelList(
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

	list, err := client.Resource(modelRegistrationGVR).
		Namespace(namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return fmt.Errorf(
			"listing ModelRegistration: %w", err,
		)
	}

	w := tabwriter.NewWriter(
		os.Stdout, 0, 4, 2, ' ', 0,
	)
	fmt.Fprintln(w, "NAME\tMODEL\tENVIRONMENT\tPHASE")
	for _, item := range list.Items {
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		modelId, _, _ := unstructured.NestedString(
			item.Object, "spec", "model", "id",
		)
		envRef, _, _ := unstructured.NestedString(
			item.Object, "spec", "environmentRef",
		)
		fmt.Fprintf(w, "%s\t%s\t%s\t%s\n",
			item.GetName(), modelId, envRef, phase)
	}
	w.Flush()
	return nil
}

func newModelRegistrationDeleteCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "delete <name>",
		Short: "Delete a ModelRegistration resource",
		Args:  cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelDelete(
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

func runModelDelete(
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

	err = client.Resource(modelRegistrationGVR).
		Namespace(namespace).
		Delete(ctx, name, metav1.DeleteOptions{})
	if err != nil {
		return fmt.Errorf(
			"deleting ModelRegistration: %w", err,
		)
	}

	fmt.Printf("ModelRegistration %q deleted\n", name)
	return nil
}

// ensureModelInfra creates Azure resources (resource
// group, storage account, managed identity) when they
// are not provided via CLI flags. This is the "simple
// mode" where the CLI auto-provisions infrastructure.
func ensureModelInfra(o *mrCreateOpts) error {
	// Get Azure context for subscription/tenant.
	azCtx, err := getAzureContext()
	if err != nil {
		return fmt.Errorf(
			"simple mode requires Azure CLI login: %w",
			err,
		)
	}

	// Derive names from the environment name.
	envName := o.env
	if o.resourceGroup == "" {
		if os.Getenv("GITHUB_ACTIONS") == "true" {
			o.resourceGroup = envName + "-" +
				os.Getenv("JOB_ID") + "-" +
				os.Getenv("RUN_ID")
		} else {
			user := os.Getenv("USER")
			if os.Getenv("CODESPACES") == "true" {
				user = os.Getenv("GITHUB_USER")
			}
			if user == "" {
				return fmt.Errorf(
					"simple mode requires " +
						"--resource-group or " +
						"USER env var to be set",
				)
			}
			o.resourceGroup = envName + "-" + user
		}
	}

	if o.location == "" {
		// Try to detect from existing RG.
		loc, locErr := getResourceGroupLocation(
			o.resourceGroup,
		)
		if locErr != nil {
			return fmt.Errorf(
				"simple mode requires --location "+
					"(resource group %q does not "+
					"exist yet)",
				o.resourceGroup,
			)
		}
		o.location = loc
	}

	// Sanitize storage account name (lowercase, no
	// hyphens, max 24 chars).
	saName := strings.ReplaceAll(
		strings.ToLower(o.resourceGroup), "-", "",
	) + "sa"
	if len(saName) > 24 {
		saName = saName[:24]
	}
	miName := o.resourceGroup + "-mi"

	// 1. Ensure resource group.
	fmt.Printf(
		"Ensuring resource group %q in %q...\n",
		o.resourceGroup, o.location,
	)
	rgArgs := []string{
		"group", "create",
		"--name", o.resourceGroup,
		"--location", o.location,
		"--output", "none",
	}
	if os.Getenv("GITHUB_ACTIONS") == "true" {
		rgArgs = append(rgArgs,
			"--tags",
			"github_actions="+
				os.Getenv("JOB_ID")+"-"+
				os.Getenv("RUN_ID"),
		)
	}
	if err := runAz(rgArgs...); err != nil {
		return fmt.Errorf(
			"creating resource group: %w", err,
		)
	}

	// 2. Ensure storage account.
	if o.storageAccountId == "" {
		fmt.Printf(
			"Ensuring storage account %q...\n", saName,
		)
		saId, err := ensureStorageAccount(
			azCtx.subscriptionId,
			o.resourceGroup,
			saName,
			o.location,
		)
		if err != nil {
			return fmt.Errorf(
				"creating storage account: %w", err,
			)
		}
		o.storageAccountId = saId

		// Assign Storage Blob Data Contributor to
		// the logged-in user so that container
		// creation (auth-mode login) and operator
		// uploads succeed.
		fmt.Println(
			"Assigning Storage Blob Data " +
				"Contributor to caller...",
		)
		if err := ensureCallerBlobRBAC(
			saId,
		); err != nil {
			return fmt.Errorf(
				"assigning blob RBAC: %w", err,
			)
		}

		// Also grant the in-cluster operator's workload
		// identity blob access, since on a self-contained
		// AKS workload cluster the operator (not the caller)
		// performs the model upload via workload identity.
		// No-op on management clusters using the caller's
		// credentials (no workload-identity annotation).
		if err := ensureOperatorBlobRBAC(
			saId,
		); err != nil {
			return fmt.Errorf(
				"assigning operator blob RBAC: %w", err,
			)
		}

		// Ensure the blob container. RBAC
		// propagation can take up to 2 minutes,
		// so retry on failure.
		fmt.Printf(
			"Creating container %q...\n",
			o.containerName,
		)
		if err := ensureBlobContainer(
			saName, o.containerName,
		); err != nil {
			return fmt.Errorf(
				"creating blob container: %w", err,
			)
		}
	}

	// 3. Ensure managed identity.
	if o.managedIdentityId == "" {
		fmt.Printf(
			"Ensuring managed identity %q...\n", miName,
		)
		miId, err := ensureManagedIdentity(
			azCtx.subscriptionId,
			o.resourceGroup,
			miName,
			o.location,
		)
		if err != nil {
			return fmt.Errorf(
				"creating managed identity: %w", err,
			)
		}
		o.managedIdentityId = miId
	}

	fmt.Println("Infrastructure ready.")
	return nil
}

// ensureStorageAccount creates a storage account if it
// does not exist and returns its ARM resource ID.
func ensureStorageAccount(
	subscriptionId string,
	resourceGroup string,
	name string,
	location string,
) (string, error) {
	// Check if it already exists to avoid the slow
	// create call.
	showOut, showErr := exec.Command(
		"az", "storage", "account", "show",
		"--name", name,
		"--resource-group", resourceGroup,
		"--output", "json",
		"--query", "id",
	).Output()
	if showErr == nil {
		id := strings.Trim(
			strings.TrimSpace(string(showOut)), "\"",
		)
		if id != "" {
			fmt.Printf(
				"Storage account %q already exists.\n",
				name,
			)
			return id, nil
		}
	}

	out, err := exec.Command(
		"az", "storage", "account", "create",
		"--name", name,
		"--resource-group", resourceGroup,
		"--location", location,
		"--sku", "Standard_LRS",
		"--kind", "StorageV2",
		"--min-tls-version", "TLS1_2",
		"--allow-shared-key-access", "false",
		"--output", "json",
		"--query", "id",
	).Output()
	if err != nil {
		return "", err
	}

	id := strings.Trim(
		strings.TrimSpace(string(out)), "\"",
	)
	if id == "" {
		return fmt.Sprintf(
			"/subscriptions/%s/resourceGroups/%s"+
				"/providers/Microsoft.Storage"+
				"/storageAccounts/%s",
			subscriptionId, resourceGroup, name,
		), nil
	}
	return id, nil
}

// ensureCallerBlobRBAC assigns Storage Blob Data
// Contributor to the currently logged-in Azure CLI
// principal on the given storage account scope. It
// handles both interactive users and service principals
// (e.g., GitHub Actions with AZURE_CLIENT_ID).
func ensureCallerBlobRBAC(saId string) error {
	objectId, principalType, err :=
		getCallerIdentity()
	if err != nil {
		return err
	}

	return runAz(
		"role", "assignment", "create",
		"--role", "Storage Blob Data Contributor",
		"--scope", saId,
		"--assignee-object-id", objectId,
		"--assignee-principal-type", principalType,
		"--output", "none",
	)
}

// ensureOperatorBlobRBAC grants the cleanroom operator's
// workload identity Storage Blob Data Contributor on the
// storage account. On a self-contained AKS workload cluster
// the in-cluster operator uploads the model via workload
// identity, so it (not the caller) needs blob data access.
// It is a no-op on management-cluster deployments where the
// operator uses the caller's credentials via credentials-proxy
// and the cleanroom-operator ServiceAccount has no
// workload-identity annotation.
func ensureOperatorBlobRBAC(saId string) error {
	clientset, err := getClientset()
	if err != nil {
		// Cannot reach the cluster; skip (best-effort).
		return nil
	}
	sa, err := clientset.CoreV1().ServiceAccounts(
		operatorNamespace,
	).Get(
		context.TODO(), "cleanroom-operator",
		metav1.GetOptions{},
	)
	if err != nil {
		return nil
	}
	clientId := sa.Annotations["azure.workload.identity/client-id"]
	if clientId == "" {
		// Credentials-proxy flow: operator == caller.
		return nil
	}

	// Resolve the managed identity's service principal
	// object ID from its client ID.
	out, err := exec.Command(
		"az", "ad", "sp", "show",
		"--id", clientId,
		"--query", "id",
		"--output", "tsv",
	).Output()
	if err != nil {
		return fmt.Errorf(
			"resolving operator identity %q: %w",
			clientId, err,
		)
	}
	objectId := strings.TrimSpace(string(out))
	if objectId == "" {
		return fmt.Errorf(
			"operator identity object ID empty for %q",
			clientId,
		)
	}

	// Idempotent: treat an existing assignment as success.
	cmdOut, err := exec.Command(
		"az", "role", "assignment", "create",
		"--role", "Storage Blob Data Contributor",
		"--scope", saId,
		"--assignee-object-id", objectId,
		"--assignee-principal-type", "ServicePrincipal",
		"--output", "none",
	).CombinedOutput()
	if err != nil &&
		!strings.Contains(string(cmdOut), "RoleAssignmentExists") {
		return fmt.Errorf(
			"granting operator blob RBAC: %s: %w",
			strings.TrimSpace(string(cmdOut)), err,
		)
	}
	return nil
}

// getCallerIdentity returns the object ID and principal
// type of the currently logged-in Azure CLI identity.
// In GitHub Actions (AZURE_CLIENT_ID set) it resolves
// the service principal; otherwise it uses the
// signed-in user.
func getCallerIdentity() (string, string, error) {
	clientId := os.Getenv("AZURE_CLIENT_ID")
	if clientId != "" {
		// Service principal flow (GitHub Actions).
		out, err := exec.Command(
			"az", "ad", "sp", "show",
			"--id", clientId,
			"--query", "id",
			"--output", "tsv",
		).Output()
		if err != nil {
			return "", "", fmt.Errorf(
				"getting SP object ID for "+
					"AZURE_CLIENT_ID %q: %w",
				clientId, err,
			)
		}
		objectId := strings.TrimSpace(string(out))
		if objectId == "" {
			return "", "", fmt.Errorf(
				"SP object ID is empty for "+
					"AZURE_CLIENT_ID %q",
				clientId,
			)
		}
		return objectId, "ServicePrincipal", nil
	}

	// Interactive user flow.
	out, err := exec.Command(
		"az", "ad", "signed-in-user", "show",
		"--query", "id",
		"--output", "tsv",
	).Output()
	if err != nil {
		return "", "", fmt.Errorf(
			"getting signed-in user: %w", err,
		)
	}
	objectId := strings.TrimSpace(string(out))
	if objectId == "" {
		return "", "", fmt.Errorf(
			"signed-in user object ID is empty",
		)
	}
	return objectId, "User", nil
}

// ensureBlobContainer creates a blob container, retrying
// for up to 120 seconds to allow RBAC propagation.
func ensureBlobContainer(
	accountName string,
	containerName string,
) error {
	timeout := 120 * time.Second
	interval := 10 * time.Second
	deadline := time.Now().Add(timeout)

	for {
		err := runAz(
			"storage", "container", "create",
			"--account-name", accountName,
			"--name", containerName,
			"--auth-mode", "login",
			"--output", "none",
		)
		if err == nil {
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf(
				"timed out after %s waiting for "+
					"RBAC propagation: %w",
				timeout, err,
			)
		}
		fmt.Printf(
			"Retrying container creation, " +
				"waiting for RBAC propagation...\n",
		)
		time.Sleep(interval)
	}
}

// ensureManagedIdentity creates a user-assigned managed
// identity if it does not exist and returns its ARM
// resource ID.
func ensureManagedIdentity(
	subscriptionId string,
	resourceGroup string,
	name string,
	location string,
) (string, error) {
	out, err := exec.Command(
		"az", "identity", "create",
		"--name", name,
		"--resource-group", resourceGroup,
		"--location", location,
		"--output", "json",
		"--query", "id",
	).Output()
	if err != nil {
		return "", err
	}

	id := strings.Trim(
		strings.TrimSpace(string(out)), "\"",
	)
	if id == "" {
		return fmt.Sprintf(
			"/subscriptions/%s/resourceGroups/%s"+
				"/providers/Microsoft."+
				"ManagedIdentity"+
				"/userAssignedIdentities/%s",
			subscriptionId, resourceGroup, name,
		), nil
	}
	return id, nil
}

// runAz runs an az CLI command and returns any error.
func runAz(args ...string) error {
	cmd := exec.Command("az", args...)
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

// mrAllConditions is the ordered list of conditions
// tracked during model governance setup.
var mrAllConditions = []string{
	"ModelUploaded",
	"FlexNodeReady",
	"CcfUserReady",
	"OidcIssuerReady",
	"AccessConfigured",
	"DatasetDocReady",
	"ModelDocReady",
}

// mrRemainingConditions returns conditions not yet True.
func mrRemainingConditions(
	done map[string]bool,
) []string {
	var remaining []string
	for _, c := range mrAllConditions {
		if !done[c] {
			remaining = append(remaining, c)
		}
	}
	return remaining
}

func runModelWait(
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
	useColor := term.IsTerminal(int(os.Stdout.Fd()))

	watcher, err := client.Resource(modelRegistrationGVR).
		Namespace(namespace).
		Watch(ctx, metav1.ListOptions{
			FieldSelector: "metadata.name=" + name,
		})
	if err != nil {
		return fmt.Errorf(
			"watching ModelRegistration: %w", err,
		)
	}
	defer watcher.Stop()

	// Watch Kubernetes Events for this
	// ModelRegistration to show upload progress.
	eventsCh := make(chan watch.Event)
	evWatcher, evErr := startMREventWatcher(
		ctx, namespace, name, eventsCh,
	)
	if evErr == nil {
		defer evWatcher.Stop()
	}

	fmt.Printf(
		"Waiting for ModelRegistration %q to reach "+
			"%s...\n",
		name, targetPhase,
	)

	// Track conditions already printed.
	printedConditions := make(map[string]string)
	doneConditions := make(map[string]bool)
	printedEvents := make(map[string]bool)
	lastPhase := ""

	for {
		select {
		case <-ctx.Done():
			return fmt.Errorf(
				"timed out waiting for "+
					"ModelRegistration %q to reach "+
					"%s after %s",
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
			obj, ok :=
				event.Object.(*unstructured.Unstructured)
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
				if p == "Configuring" {
					fmt.Printf("  %-8s %s\n",
						elapsed,
						"Configuring started")
				} else if p != "Ready" &&
					p != "Failed" {
					fmt.Printf("  %-8s Phase: %s\n",
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
						mrRemainingConditions(
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
					if cReason == "Failed" ||
						cReason == "Error" {
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
						elapsed, icon, cType, msg,
					)
					if useColor && icon == "✗" {
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
						"ModelRegistration %q "+
							"reached %s (%s, "+
							"trace: %s)\n",
						name, targetPhase,
						elapsed, traceId,
					)
				} else {
					fmt.Printf(
						"ModelRegistration %q "+
							"reached %s (%s)\n",
						name, targetPhase,
						elapsed,
					)
				}
				return nil
			}
			if p == "Failed" {
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
						"ModelRegistration %q "+
							"failed after %s: "+
							"%s%s",
						name, elapsed,
						msg, traceInfo,
					)
				}
				return fmt.Errorf(
					"ModelRegistration %q failed "+
						"after %s%s",
					name, elapsed, traceInfo,
				)
			}
		}
	}
}

// startMREventWatcher watches Kubernetes Events for a
// specific ModelRegistration and forwards them to the
// provided channel. Only new events are forwarded
// (history is skipped via resourceVersion).
func startMREventWatcher(
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
			"involvedObject.kind=ModelRegistration",
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

func newModelRegistrationStatusCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "status <name>",
		Short: "Show detailed status of a ModelRegistration",
		Args:  cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelRegistrationStatus(
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

func runModelRegistrationStatus(
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

	result, err := client.Resource(modelRegistrationGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting ModelRegistration: %w", err,
		)
	}

	phase, _, _ := unstructured.NestedString(
		result.Object, "status", "phase",
	)
	modelId, _, _ := unstructured.NestedString(
		result.Object, "spec", "model", "id",
	)
	envRef, _, _ := unstructured.NestedString(
		result.Object, "spec", "environmentRef",
	)

	fmt.Printf("ModelRegistration: %s\n", name)
	fmt.Printf("  Model:       %s\n", modelId)
	fmt.Printf("  Environment: %s\n", envRef)
	fmt.Printf("  Phase:       %s\n", phase)

	if phase == "Failed" {
		msg, _, _ := unstructured.NestedString(
			result.Object, "status", "message",
		)
		if msg != "" {
			fmt.Printf("  Message:     %s\n", msg)
		}
	}

	traceId, _, _ := unstructured.NestedString(
		result.Object,
		"status", "lastOperationTraceId",
	)
	if traceId != "" {
		fmt.Printf("  TraceId:     %s\n", traceId)
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
			cStatus, _ := cMap["status"].(string)
			cReason, _ := cMap["reason"].(string)
			cMsg, _ := cMap["message"].(string)
			indicator := "?"
			if cStatus == "True" {
				indicator = "✓"
			} else if cStatus == "False" {
				if strings.Contains(
					cReason, "Failed") {
					indicator = "✗"
				} else {
					indicator = "○"
				}
			}
			// Truncate long messages.
			if len(cMsg) > 120 {
				cMsg = cMsg[:117] + "..."
			}
			fmt.Fprintf(w, "  %s\t%s\t%s\t%s\t%s\n",
				indicator, cType, cStatus,
				cReason, cMsg)
		}
		w.Flush()
	}

	return nil
}

func newModelRegistrationWaitCmd() *cobra.Command {
	var namespace string
	var forPhase string
	var timeout string

	cmd := &cobra.Command{
		Use:   "wait <name>",
		Short: "Wait for a ModelRegistration to reach a phase",
		Args:  cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			if namespace == "" {
				namespace =
					defaultNamespaceFromKubeconfig()
			}
			return runModelWait(
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

func newModelRegistrationReconcileCmd() *cobra.Command {
	var namespace string
	var noWait bool
	var timeout string

	cmd := &cobra.Command{
		Use:     "reconcile <name>",
		Aliases: []string{"retry"},
		Short:   "Trigger reconciliation of a ModelRegistration",
		Long: `Force the operator to re-evaluate the
ModelRegistration. Sets the
reconcile.cleanroom.azure.com/requestedAt annotation
and optionally waits for the resource to reach Ready.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runModelRegistrationReconcile(
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

func runModelRegistrationReconcile(
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

	md, err := client.Resource(modelRegistrationGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting ModelRegistration: %w", err,
		)
	}

	phase, _, _ := unstructured.NestedString(
		md.Object, "status", "phase",
	)

	// Set reconcile timestamp annotation.
	requestedAt := time.Now().UTC().Format(
		time.RFC3339Nano,
	)
	annotations := md.GetAnnotations()
	if annotations == nil {
		annotations = map[string]string{}
	}
	annotations["reconcile.cleanroom.azure.com/requestedAt"] = requestedAt
	md.SetAnnotations(annotations)

	_, err = client.Resource(modelRegistrationGVR).
		Namespace(namespace).
		Update(ctx, md, metav1.UpdateOptions{})
	if err != nil {
		return fmt.Errorf(
			"setting reconcile annotation: %w", err,
		)
	}

	fmt.Printf(
		"► reconciliation triggered for "+
			"ModelRegistration %q (was: %s)\n",
		name, phase,
	)

	if wait {
		// Poll until the controller acknowledges the
		// reconcile request before starting the watch.
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
						"controller to acknowledge " +
						"reconcile request",
				)
			case <-ticker.C:
				latest, err := client.Resource(
					modelRegistrationGVR,
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
					return runModelWait(
						ctx, name, namespace,
						"Ready", timeout,
					)
				}
			}
		}
	}

	return nil
}
