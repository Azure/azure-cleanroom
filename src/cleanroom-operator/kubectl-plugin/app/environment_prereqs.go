package app

import (
	"context"
	"fmt"

	"github.com/spf13/cobra"

	"github.com/Azure/azure-cleanroom/cleanroom-operator/kubectl-plugin/prereqs"
)

type preparePrereqsOpts struct {
	resourceGroup         string
	ccfResourceGroup      string
	location              string
	subscription          string
	tenantId              string
	namespace             string
	yes                   bool
	setupWorkloadIdentity bool
	aksClusterName        string
	identityName          string
	operatorNamespace     string
	oidcStorageAccountId  string
	rbacScope             string
}

func newEnvironmentPreparePrereqsCmd() *cobra.Command {
	o := &preparePrereqsOpts{}

	cmd := &cobra.Command{
		Use:   "prepare-prereqs <name>",
		Short: "Prepare Azure prerequisites for an AKS environment",
		Long: `Create Azure resource groups and a CCF storage
account, then store the resulting provider configuration
in a Kubernetes ConfigMap. Use the ConfigMap name with
'environment create --prereqs-config <name>'.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runPreparePrereqs(
				cmd.Context(), args[0], o,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(&o.resourceGroup,
		"resource-group", "",
		"Azure resource group for the AKS cluster "+
			"(defaults to <name>-cluster-<USER>)")
	f.StringVar(&o.ccfResourceGroup,
		"ccf-resource-group", "",
		"Azure resource group for the CCF network "+
			"(defaults to <name>-ccf-<USER>)")
	f.StringVar(&o.location,
		"location", "",
		"Azure region (required)")
	_ = cmd.MarkFlagRequired("location")
	f.StringVar(&o.subscription,
		"subscription", "",
		"Azure subscription ID "+
			"(auto-detected from az CLI)")
	f.StringVar(&o.tenantId,
		"tenant-id", "",
		"Azure tenant ID "+
			"(auto-detected from az CLI)")
	f.StringVar(&o.namespace, "namespace",
		defaultNamespaceFromKubeconfig(),
		"Kubernetes namespace for the ConfigMap")
	f.BoolVarP(&o.yes, "yes", "y", false,
		"Skip confirmation prompt")
	f.BoolVar(&o.setupWorkloadIdentity,
		"setup-workload-identity", false,
		"Create a managed identity federated with the "+
			"operator and provider-client service accounts, "+
			"grant it RBAC, and record its client ID in the "+
			"ConfigMap. Use for self-contained AKS workload "+
			"clusters where the provider clients run "+
			"in-cluster. Requires --aks-cluster-name.")
	f.StringVar(&o.aksClusterName,
		"aks-cluster-name", "",
		"Name of the existing AKS cluster whose OIDC issuer "+
			"is used for federation "+
			"(required with --setup-workload-identity)")
	f.StringVar(&o.identityName,
		"identity-name", prereqs.DefaultIdentityName,
		"Name of the user-assigned managed identity to create")
	f.StringVar(&o.operatorNamespace,
		"operator-namespace", prereqs.DefaultOperatorNamespace,
		"Namespace where the operator and provider-client "+
			"service accounts live")
	f.StringVar(&o.oidcStorageAccountId,
		"oidc-storage-account-id", prereqs.DefaultOidcStorageAccountId,
		"Resource ID of the OIDC issuer storage account to "+
			"grant 'Storage Blob Data Contributor' on (for "+
			"OIDC document uploads). Defaults to the shared "+
			"'cleanroomoidc' account used across runs.")
	f.StringVar(&o.rbacScope,
		"rbac-scope", "subscription",
		"Scope for Contributor/User Access Administrator "+
			"grants: 'subscription' (default) or "+
			"'resource-group'")
	return cmd
}

func runPreparePrereqs(
	ctx context.Context,
	name string,
	o *preparePrereqsOpts,
) error {
	clientset, err := getClientset()
	if err != nil {
		return fmt.Errorf(
			"creating Kubernetes client: %w", err,
		)
	}

	po := &prereqs.Options{
		Name:                  name,
		ResourceGroup:         o.resourceGroup,
		CcfResourceGroup:      o.ccfResourceGroup,
		Location:              o.location,
		SubscriptionId:        o.subscription,
		TenantId:              o.tenantId,
		Namespace:             o.namespace,
		Clientset:             clientset,
		Yes:                   o.yes,
		SetupWorkloadIdentity: o.setupWorkloadIdentity,
		AksClusterName:        o.aksClusterName,
		IdentityName:          o.identityName,
		OperatorNamespace:     o.operatorNamespace,
		OidcStorageAccountId:  o.oidcStorageAccountId,
		RbacScope:             o.rbacScope,
	}

	if err := prereqs.Run(ctx, po); err != nil {
		return err
	}

	if o.setupWorkloadIdentity {
		fmt.Printf(
			"Install the operator with the federated "+
				"identity via: kubectl cleanroom install "+
				"--env-file <env-file> "+
				"--provider-virtual=false "+
				"--prereqs-config %s\n",
			name,
		)
	}

	fmt.Printf(
		"Use it with: kubectl cleanroom environment "+
			"create <env-name> --infra-type aks "+
			"--prereqs-config %s\n",
		name,
	)

	return nil
}
