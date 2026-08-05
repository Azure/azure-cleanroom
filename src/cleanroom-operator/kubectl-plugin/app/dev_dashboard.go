package app

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"syscall"

	"github.com/spf13/cobra"
	"k8s.io/client-go/tools/clientcmd"

	"github.com/Azure/azure-cleanroom/cleanroom-operator/kubectl-plugin/dashboard"
)

func newDevDashboardCmd() *cobra.Command {
	var port int
	var namespace string

	cmd := &cobra.Command{
		Use:   "dashboard",
		Short: "Open the operator dashboard UI",
		Long: `Starts a local web server that provides a
real-time dashboard for monitoring environments,
viewing resource topology, conditions, and events.
Press Ctrl+C to stop.`,
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runDashboard(
				cmd.Context(), port, namespace,
			)
		},
	}

	f := cmd.Flags()
	f.IntVar(
		&port, "port", 9090,
		"Local port for the dashboard",
	)
	f.StringVar(
		&namespace, "namespace", "default",
		"Namespace to watch for resources",
	)
	return cmd
}

func runDashboard(
	ctx context.Context,
	port int,
	namespace string,
) error {
	loadingRules :=
		clientcmd.NewDefaultClientConfigLoadingRules()
	if kubeconfigPath != "" {
		loadingRules.ExplicitPath = kubeconfigPath
	}
	kubeConfig :=
		clientcmd.NewNonInteractiveDeferredLoadingClientConfig(
			loadingRules,
			&clientcmd.ConfigOverrides{},
		)

	config, err := kubeConfig.ClientConfig()
	if err != nil {
		return fmt.Errorf(
			"loading kubeconfig: %w", err,
		)
	}

	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	srv, err := dashboard.NewServer(config, namespace)
	if err != nil {
		return fmt.Errorf(
			"creating dashboard server: %w", err,
		)
	}

	localPort, err := srv.Run(ctx, port)
	if err != nil {
		return fmt.Errorf(
			"starting dashboard: %w", err,
		)
	}

	fmt.Printf(
		"Dashboard: http://localhost:%d\n", localPort,
	)
	fmt.Println("Press Ctrl+C to stop")

	sigCh := make(chan os.Signal, 1)
	signal.Notify(
		sigCh, syscall.SIGINT, syscall.SIGTERM,
	)
	<-sigCh

	cancel()
	return nil
}
