// Package prereqs provides shared logic for preparing
// Azure prerequisites (resource groups, storage account,
// ConfigMap) needed by AKS-based clean room
// environments.
package prereqs

import (
	"bufio"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"strings"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/client-go/kubernetes"
)

// AzureContext holds the subscription and tenant
// information from the az CLI.
type AzureContext struct {
	SubscriptionId   string
	SubscriptionName string
	TenantId         string
	TenantName       string
}

// Options configures the prepare-prereqs operation.
type Options struct {
	// Name is the environment/ConfigMap name.
	Name string

	// ResourceGroup is the Azure resource group for
	// the AKS cluster. If empty, derived from Name
	// and the current user.
	ResourceGroup string

	// CcfResourceGroup is the Azure resource group
	// for the CCF network. If empty, derived from
	// Name and the current user.
	CcfResourceGroup string

	// Location is the Azure region (required).
	Location string

	// SubscriptionId overrides auto-detection.
	SubscriptionId string

	// TenantId overrides auto-detection.
	TenantId string

	// Namespace is the Kubernetes namespace for the
	// ConfigMap.
	Namespace string

	// Clientset is the Kubernetes client to use for
	// creating the ConfigMap. Required.
	Clientset kubernetes.Interface

	// Yes skips the confirmation prompt when true.
	Yes bool

	// SetupWorkloadIdentity, when true, creates a
	// user-assigned managed identity federated with the
	// operator and provider-client service accounts on the
	// AKS cluster, grants it RBAC, and records its client ID
	// in the prereqs ConfigMap (key
	// "workloadIdentityClientId"). Required for the
	// self-contained (in-cluster provider) AKS topology where
	// the provider clients run on the workload cluster and
	// have no host az CLI credentials.
	SetupWorkloadIdentity bool

	// AksClusterName is the name of the existing AKS cluster
	// whose OIDC issuer is used to federate the service
	// accounts. Required when SetupWorkloadIdentity is true.
	AksClusterName string

	// IdentityName is the name of the user-assigned managed
	// identity to create. Defaults to
	// "cleanroom-provider-identity".
	IdentityName string

	// OperatorNamespace is the namespace where the operator
	// and provider-client service accounts live. Defaults to
	// "cleanroom-system".
	OperatorNamespace string

	// OidcStorageAccountId, when set, grants the managed
	// identity "Storage Blob Data Contributor" on that
	// storage account so the operator can upload OIDC
	// documents. Optional (the OIDC issuer storage account is
	// often a pre-existing shared account).
	OidcStorageAccountId string

	// RbacScope controls the scope of the Contributor and
	// User Access Administrator role assignments granted to
	// the managed identity: "subscription" (default, broad,
	// covers the AKS node MC_ resource group) or
	// "resource-group" (scoped to the cluster and CCF
	// resource groups only).
	RbacScope string
}

const (
	// DefaultIdentityName is the default user-assigned managed
	// identity name created for workload-identity federation.
	DefaultIdentityName = "cleanroom-provider-identity"

	// DefaultOperatorNamespace is the namespace where the
	// operator and provider-client service accounts live.
	DefaultOperatorNamespace = "cleanroom-system"

	// WorkloadIdentityClientIDKey is the prereqs ConfigMap key
	// under which the managed identity client ID is stored.
	WorkloadIdentityClientIDKey = "workloadIdentityClientId"

	// DefaultOidcStorageAccountId is the shared storage account
	// the governance service uploads OIDC documents to. It is
	// used across all runs today, so it is the default target
	// for the "Storage Blob Data Contributor" grant when
	// --oidc-storage-account-id is not supplied. Override the
	// flag to point at a different account.
	DefaultOidcStorageAccountId = "/subscriptions/" +
		"fccb68eb-8ccf-49a6-a69a-7ea3c2867e9c" +
		"/resourceGroups/azcleanroom-ctest-rg" +
		"/providers/Microsoft.Storage/storageAccounts/cleanroomoidc"
)

// federatedServiceAccounts are the service accounts that the
// managed identity is federated with. These must match the
// service account names created by the operator Helm chart.
var federatedServiceAccounts = []string{
	"cleanroom-operator",
	"cluster-provider-client",
	"ccf-provider-client",
}

// GetAzureContext runs 'az account show' to get the
// current subscription and tenant.
func GetAzureContext(
	ctx context.Context,
) (*AzureContext, error) {
	out, err := exec.CommandContext(
		ctx, "az", "account", "show",
		"--output", "json",
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

	return &AzureContext{
		SubscriptionId:   account.ID,
		SubscriptionName: account.Name,
		TenantId:         account.TenantID,
		TenantName:       account.TenantDisplayName,
	}, nil
}

// DeriveResourceGroups fills in ResourceGroup and
// CcfResourceGroup if they are empty, using the
// environment name and the current OS user.
func DeriveResourceGroups(o *Options) error {
	if o.ResourceGroup != "" &&
		o.CcfResourceGroup != "" {
		return nil
	}

	suffix, err := userSuffix()
	if err != nil {
		return err
	}

	if o.ResourceGroup == "" {
		o.ResourceGroup = o.Name +
			"-cluster-" + suffix
	}
	if o.CcfResourceGroup == "" {
		o.CcfResourceGroup = o.Name +
			"-ccf-" + suffix
	}
	return nil
}

// DeriveStorageAccountName returns the deterministic
// storage account name for the given CCF resource group.
func DeriveStorageAccountName(
	ccfResourceGroup string,
) string {
	h := sha256.Sum256([]byte(ccfResourceGroup))
	unique := hex.EncodeToString(h[:])[:8]
	return "ccf" + unique + "sa"
}

// Run executes the full prepare-prereqs flow: creates
// Azure resource groups, a storage account, and a
// Kubernetes ConfigMap with the provider configuration.
func Run(ctx context.Context, o *Options) error {
	if o.Location == "" {
		return fmt.Errorf("location is required")
	}
	if o.Clientset == nil {
		return fmt.Errorf("clientset is required")
	}
	if o.Namespace == "" {
		return fmt.Errorf("namespace is required")
	}
	if o.SetupWorkloadIdentity && o.AksClusterName == "" {
		return fmt.Errorf(
			"--aks-cluster-name is required when " +
				"--setup-workload-identity is set",
		)
	}
	if o.IdentityName == "" {
		o.IdentityName = DefaultIdentityName
	}
	if o.OperatorNamespace == "" {
		o.OperatorNamespace = DefaultOperatorNamespace
	}
	if o.RbacScope == "" {
		o.RbacScope = "subscription"
	}

	if err := DeriveResourceGroups(o); err != nil {
		return err
	}

	// Auto-detect subscription and tenant if needed.
	sub := o.SubscriptionId
	tenant := o.TenantId
	var azCtx *AzureContext
	if sub == "" || tenant == "" {
		var err error
		azCtx, err = GetAzureContext(ctx)
		if err != nil {
			missing := []string{}
			if sub == "" {
				missing = append(
					missing, "subscription",
				)
			}
			if tenant == "" {
				missing = append(
					missing, "tenant-id",
				)
			}
			return fmt.Errorf(
				"could not auto-detect Azure "+
					"context (%s); log in with "+
					"'az login': %w",
				strings.Join(missing, ", "), err,
			)
		}
		if sub == "" {
			sub = azCtx.SubscriptionId
		}
		if tenant == "" {
			tenant = azCtx.TenantId
		}
	}

	// Confirm before creating Azure resources.
	if !o.Yes &&
		os.Getenv("GITHUB_ACTIONS") != "true" {
		subName := ""
		if azCtx != nil {
			subName = azCtx.SubscriptionName
		}
		if subName != "" {
			fmt.Printf(
				"This command will create "+
					"resource groups and a "+
					"storage account in "+
					"subscription %q (%s).\n",
				subName, sub,
			)
		} else {
			fmt.Printf(
				"This command will create "+
					"resource groups and a "+
					"storage account in "+
					"subscription %s.\n",
				sub,
			)
		}
		fmt.Print(
			"Press Y to continue or N to abort: ",
		)
		reader := bufio.NewReader(os.Stdin)
		answer, _ := reader.ReadString('\n')
		answer = strings.TrimSpace(
			strings.ToLower(answer),
		)
		if answer != "y" && answer != "yes" {
			return fmt.Errorf("aborted by user")
		}
		fmt.Println()
	}

	fmt.Printf(
		"Using resource group %q\n",
		o.ResourceGroup,
	)
	fmt.Printf(
		"Using CCF resource group %q\n",
		o.CcfResourceGroup,
	)

	// Create resource groups.
	if err := ensureResourceGroup(
		ctx, o.ResourceGroup, o.Location,
	); err != nil {
		return err
	}

	if o.CcfResourceGroup != o.ResourceGroup {
		if err := ensureResourceGroup(
			ctx, o.CcfResourceGroup, o.Location,
		); err != nil {
			return err
		}
	}

	// Create storage account.
	saName := DeriveStorageAccountName(
		o.CcfResourceGroup,
	)

	fmt.Printf(
		"Ensuring storage account %q in "+
			"resource group %q...\n",
		saName, o.CcfResourceGroup,
	)

	saId, err := ensureStorageAccount(
		ctx, saName, o.CcfResourceGroup,
		o.Location, sub,
	)
	if err != nil {
		return err
	}

	// Build provider configs.
	ccfJSON, _ := json.Marshal(
		map[string]interface{}{
			"subscriptionId":    sub,
			"resourceGroupName": o.CcfResourceGroup,
			"location":          o.Location,
			"tenantId":          tenant,
			"azureFiles": map[string]interface{}{
				"storageAccountId": saId,
			},
		},
	)

	clusterJSON, _ := json.Marshal(
		map[string]interface{}{
			"subscriptionId":    sub,
			"resourceGroupName": o.ResourceGroup,
			"location":          o.Location,
			"tenantId":          tenant,
		},
	)

	cmData := map[string]string{
		"ccfProviderConfig":     string(ccfJSON),
		"clusterProviderConfig": string(clusterJSON),
	}

	// Optionally set up AKS Workload Identity: create a
	// managed identity, federate the operator/provider-client
	// service accounts, grant RBAC, and record the client ID.
	if o.SetupWorkloadIdentity {
		clientID, err := setupWorkloadIdentity(ctx, o, sub)
		if err != nil {
			return fmt.Errorf(
				"setting up workload identity: %w", err,
			)
		}
		cmData[WorkloadIdentityClientIDKey] = clientID
	}

	// Create or update the ConfigMap.
	cm := &corev1.ConfigMap{
		ObjectMeta: metav1.ObjectMeta{
			Name:      o.Name,
			Namespace: o.Namespace,
			Labels: map[string]string{
				"cleanroom.azure.com/prereqs": "true",
			},
		},
		Data: cmData,
	}

	_, err = o.Clientset.CoreV1().ConfigMaps(
		o.Namespace,
	).Create(ctx, cm, metav1.CreateOptions{})
	if err != nil {
		if apierrors.IsAlreadyExists(err) {
			_, err = o.Clientset.CoreV1().ConfigMaps(
				o.Namespace,
			).Update(
				ctx, cm, metav1.UpdateOptions{},
			)
			if err != nil {
				return fmt.Errorf(
					"updating prereqs ConfigMap: %w",
					err,
				)
			}
			fmt.Printf(
				"Prereqs ConfigMap %s updated "+
					"in namespace %s\n",
				o.Name, o.Namespace,
			)
		} else {
			return fmt.Errorf(
				"creating prereqs ConfigMap: %w",
				err,
			)
		}
	} else {
		fmt.Printf(
			"Prereqs ConfigMap %s created "+
				"in namespace %s\n",
			o.Name, o.Namespace,
		)
	}

	return nil
}

func userSuffix() (string, error) {
	if os.Getenv("GITHUB_ACTIONS") == "true" {
		return os.Getenv("JOB_ID") + "-" +
			os.Getenv("RUN_ID"), nil
	}

	user := os.Getenv("USER")
	if os.Getenv("CODESPACES") == "true" {
		user = os.Getenv("GITHUB_USER")
	}
	if user == "" {
		return "", fmt.Errorf(
			"USER env var must be set to derive " +
				"resource group names",
		)
	}
	return user, nil
}

func ensureResourceGroup(
	ctx context.Context,
	name string,
	location string,
) error {
	fmt.Printf(
		"Ensuring resource group %q in %s...\n",
		name, location,
	)

	out, err := exec.CommandContext(
		ctx, "az", "group", "create",
		"--name", name,
		"--location", location,
		"--output", "none",
	).CombinedOutput()
	if err != nil {
		return fmt.Errorf(
			"creating resource group %q: %s: %w",
			name,
			strings.TrimSpace(string(out)),
			err,
		)
	}
	return nil
}

func ensureStorageAccount(
	ctx context.Context,
	name string,
	resourceGroup string,
	location string,
	subscriptionId string,
) (string, error) {
	// Check if it already exists.
	showOut, showErr := exec.CommandContext(
		ctx, "az", "storage", "account", "show",
		"--name", name,
		"--resource-group", resourceGroup,
		"--output", "tsv",
		"--query", "id",
	).Output()
	if showErr == nil {
		id := strings.TrimSpace(string(showOut))
		if id != "" {
			return id, nil
		}
	}

	out, err := exec.CommandContext(
		ctx, "az", "storage", "account", "create",
		"--name", name,
		"--resource-group", resourceGroup,
		"--location", location,
		"--sku", "Standard_LRS",
		"--allow-shared-key-access", "true",
		"--allow-blob-public-access", "false",
		"--output", "tsv",
		"--query", "id",
	).Output()
	if err != nil {
		return "", fmt.Errorf(
			"creating storage account %q: %w",
			name, err,
		)
	}

	id := strings.TrimSpace(string(out))
	if id == "" {
		return "", fmt.Errorf(
			"empty storage account ID returned",
		)
	}
	return id, nil
}

// setupWorkloadIdentity creates (idempotently) a user-assigned
// managed identity, federates it with the operator and
// provider-client service accounts using the AKS cluster's OIDC
// issuer, grants it RBAC, and returns its client ID.
func setupWorkloadIdentity(
	ctx context.Context,
	o *Options,
	subscriptionId string,
) (string, error) {
	fmt.Printf(
		"Ensuring managed identity %q in resource "+
			"group %q...\n",
		o.IdentityName, o.ResourceGroup,
	)
	clientID, principalID, err := ensureManagedIdentity(
		ctx, o.IdentityName, o.ResourceGroup, subscriptionId,
	)
	if err != nil {
		return "", err
	}

	issuer, err := getAksOidcIssuer(
		ctx, o.AksClusterName, o.ResourceGroup, subscriptionId,
	)
	if err != nil {
		return "", err
	}

	for _, sa := range federatedServiceAccounts {
		subject := fmt.Sprintf(
			"system:serviceaccount:%s:%s",
			o.OperatorNamespace, sa,
		)
		fmt.Printf(
			"Federating service account %q...\n", subject,
		)
		if err := ensureFederatedCredential(
			ctx, sa, o.IdentityName, o.ResourceGroup,
			subscriptionId, issuer, subject,
		); err != nil {
			return "", err
		}
	}

	// Grant RBAC. Contributor manages the resources; User
	// Access Administrator is required for the provider to
	// create the role assignments that grant the AKS kubelet
	// and cluster managed identities their permissions.
	var scopes []string
	if o.RbacScope == "resource-group" {
		scopes = []string{
			fmt.Sprintf(
				"/subscriptions/%s/resourceGroups/%s",
				subscriptionId, o.ResourceGroup,
			),
		}
		if o.CcfResourceGroup != o.ResourceGroup {
			scopes = append(scopes, fmt.Sprintf(
				"/subscriptions/%s/resourceGroups/%s",
				subscriptionId, o.CcfResourceGroup,
			))
		}
	} else {
		scopes = []string{
			"/subscriptions/" + subscriptionId,
		}
	}

	for _, scope := range scopes {
		for _, role := range []string{
			"Contributor",
			"User Access Administrator",
		} {
			fmt.Printf(
				"Granting %q on %s...\n", role, scope,
			)
			if err := ensureRoleAssignment(
				ctx, principalID, role, scope,
				subscriptionId,
			); err != nil {
				return "", err
			}
		}
	}

	// Data-plane access for OIDC document uploads. Contributor
	// (control plane) does not grant blob data access.
	if o.OidcStorageAccountId != "" {
		fmt.Printf(
			"Granting %q on OIDC storage account...\n",
			"Storage Blob Data Contributor",
		)
		if err := ensureRoleAssignment(
			ctx, principalID,
			"Storage Blob Data Contributor",
			o.OidcStorageAccountId, subscriptionId,
		); err != nil {
			return "", err
		}
	}

	fmt.Printf(
		"Managed identity client ID: %s\n"+
			"(RBAC propagation may take a minute.)\n",
		clientID,
	)
	return clientID, nil
}

// ensureManagedIdentity creates the user-assigned managed
// identity if it does not exist and returns its client ID and
// principal (object) ID.
func ensureManagedIdentity(
	ctx context.Context,
	name string,
	resourceGroup string,
	subscriptionId string,
) (clientID, principalID string, err error) {
	type identity struct {
		ClientID    string `json:"clientId"`
		PrincipalID string `json:"principalId"`
	}

	// az identity create is idempotent: it returns the
	// existing identity if it already exists.
	out, err := exec.CommandContext(
		ctx, "az", "identity", "create",
		"--name", name,
		"--resource-group", resourceGroup,
		"--subscription", subscriptionId,
		"--output", "json",
	).Output()
	if err != nil {
		return "", "", fmt.Errorf(
			"creating managed identity %q: %w", name, err,
		)
	}

	var id identity
	if err := json.Unmarshal(out, &id); err != nil {
		return "", "", fmt.Errorf(
			"parsing managed identity output: %w", err,
		)
	}
	if id.ClientID == "" || id.PrincipalID == "" {
		return "", "", fmt.Errorf(
			"managed identity %q missing clientId/"+
				"principalId", name,
		)
	}
	return id.ClientID, id.PrincipalID, nil
}

// getAksOidcIssuer returns the OIDC issuer URL of the AKS
// cluster, which is required to federate service accounts.
func getAksOidcIssuer(
	ctx context.Context,
	aksClusterName string,
	resourceGroup string,
	subscriptionId string,
) (string, error) {
	out, err := exec.CommandContext(
		ctx, "az", "aks", "show",
		"--name", aksClusterName,
		"--resource-group", resourceGroup,
		"--subscription", subscriptionId,
		"--query", "oidcIssuerProfile.issuerUrl",
		"--output", "tsv",
	).Output()
	if err != nil {
		return "", fmt.Errorf(
			"reading OIDC issuer of AKS cluster %q "+
				"(is the OIDC issuer enabled?): %w",
			aksClusterName, err,
		)
	}
	issuer := strings.TrimSpace(string(out))
	if issuer == "" {
		return "", fmt.Errorf(
			"AKS cluster %q has no OIDC issuer URL; "+
				"enable the OIDC issuer and workload "+
				"identity on the cluster",
			aksClusterName,
		)
	}
	return issuer, nil
}

// ensureFederatedCredential creates a federated identity
// credential linking the managed identity to a Kubernetes
// service account subject, if it does not already exist.
func ensureFederatedCredential(
	ctx context.Context,
	name string,
	identityName string,
	resourceGroup string,
	subscriptionId string,
	issuer string,
	subject string,
) error {
	// Idempotency: skip creation if it already exists.
	showErr := exec.CommandContext(
		ctx, "az", "identity", "federated-credential", "show",
		"--name", name,
		"--identity-name", identityName,
		"--resource-group", resourceGroup,
		"--subscription", subscriptionId,
		"--output", "none",
	).Run()
	if showErr == nil {
		return nil
	}

	out, err := exec.CommandContext(
		ctx, "az", "identity", "federated-credential", "create",
		"--name", name,
		"--identity-name", identityName,
		"--resource-group", resourceGroup,
		"--subscription", subscriptionId,
		"--issuer", issuer,
		"--subject", subject,
		"--audience", "api://AzureADTokenExchange",
		"--output", "none",
	).CombinedOutput()
	if err != nil {
		return fmt.Errorf(
			"creating federated credential %q: %s: %w",
			name, strings.TrimSpace(string(out)), err,
		)
	}
	return nil
}

// ensureRoleAssignment grants the managed identity a role at a
// scope, treating an already-existing assignment as success.
// The identity's principal (object) ID is used directly with
// an explicit principal type to avoid Microsoft Graph lookup
// delays for a freshly created identity.
func ensureRoleAssignment(
	ctx context.Context,
	principalID string,
	role string,
	scope string,
	subscriptionId string,
) error {
	out, err := exec.CommandContext(
		ctx, "az", "role", "assignment", "create",
		"--assignee-object-id", principalID,
		"--assignee-principal-type", "ServicePrincipal",
		"--role", role,
		"--scope", scope,
		"--subscription", subscriptionId,
		"--output", "none",
	).CombinedOutput()
	if err != nil {
		msg := strings.TrimSpace(string(out))
		// Treat an already-existing assignment as success.
		if strings.Contains(msg, "RoleAssignmentExists") {
			return nil
		}
		return fmt.Errorf(
			"granting role %q on %s: %s: %w",
			role, scope, msg, err,
		)
	}
	return nil
}
