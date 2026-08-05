package app

import (
	"github.com/spf13/cobra"
)

// operatorNamespace is the Kubernetes namespace where the
// cleanroom operator and its infrastructure are deployed.
const operatorNamespace = "cleanroom-system"

// kubeconfigPath is the path to the kubeconfig file set via
// the --kubeconfig persistent flag.
var kubeconfigPath string

// NewRootCmd creates the root cobra command for kubectl-cleanroom.
func NewRootCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "kubectl-cleanroom",
		Short: "Manage Clean Room clusters via Kubernetes CRDs",
		Long: `kubectl cleanroom is a kubectl plugin for managing
Clean Room cluster lifecycle using Kubernetes custom resources.`,
		SilenceUsage: true,
	}

	cmd.PersistentFlags().StringVar(
		&kubeconfigPath, "kubeconfig", "",
		"Path to the kubeconfig file to use",
	)

	cmd.AddCommand(newInstallCmd())
	cmd.AddCommand(newUninstallCmd())
	cmd.AddCommand(newDevCmd())
	cmd.AddCommand(newBootstrapCmd())
	cmd.AddCommand(newInfoCmd())
	cmd.AddCommand(newClusterCmd())
	cmd.AddCommand(newCcfNetworkCmd())
	cmd.AddCommand(newCcfUserCmd())
	cmd.AddCommand(newEnvironmentCmd())
	cmd.AddCommand(newModelRegistrationCmd())
	cmd.AddCommand(newModelDeploymentCmd())
	return cmd
}

func newClusterCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "cluster",
		Short: "Manage Clean Room clusters",
	}

	cmd.AddCommand(newClusterCreateCmd())
	cmd.AddCommand(newClusterUpdateCmd())
	cmd.AddCommand(newClusterGetCmd())
	cmd.AddCommand(newClusterDeleteCmd())
	cmd.AddCommand(newClusterListCmd())
	cmd.AddCommand(newClusterKubeconfigCmd())
	cmd.AddCommand(newClusterWaitCmd())
	cmd.AddCommand(newClusterHealthCmd())
	cmd.AddCommand(newClusterReconcileCmd())
	return cmd
}
