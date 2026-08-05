package main

import (
	"context"
	"crypto/tls"
	"flag"
	"os"

	_ "k8s.io/client-go/plugin/pkg/client/auth"

	"k8s.io/apimachinery/pkg/runtime"
	utilruntime "k8s.io/apimachinery/pkg/util/runtime"
	clientgoscheme "k8s.io/client-go/kubernetes/scheme"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/healthz"
	"sigs.k8s.io/controller-runtime/pkg/log/zap"
	"sigs.k8s.io/controller-runtime/pkg/manager"
	metricsserver "sigs.k8s.io/controller-runtime/pkg/metrics/server"

	cleanroomv1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	airunwayv1alpha1 "github.com/kaito-project/airunway/controller/api/v1alpha1"

	provider "github.com/Azure/azure-cleanroom/cleanroom-operator/providers/airunway"
)

var (
	scheme   = runtime.NewScheme()
	setupLog = ctrl.Log.WithName("setup")
)

func init() {
	utilruntime.Must(
		clientgoscheme.AddToScheme(scheme))
	utilruntime.Must(
		airunwayv1alpha1.AddToScheme(scheme))
	utilruntime.Must(
		cleanroomv1alpha1.AddToScheme(scheme))
}

func main() {
	var metricsAddr string
	var probeAddr string
	var enableLeaderElection bool

	flag.StringVar(&metricsAddr,
		"metrics-bind-address", ":8443",
		"The metrics endpoint bind address.")
	flag.StringVar(&probeAddr,
		"health-probe-bind-address", ":8081",
		"The probe endpoint bind address.")
	flag.BoolVar(&enableLeaderElection,
		"leader-elect", false,
		"Enable leader election.")

	opts := zap.Options{Development: true}
	opts.BindFlags(flag.CommandLine)
	flag.Parse()

	ctrl.SetLogger(zap.New(zap.UseFlagOptions(&opts)))

	tlsOpts := []func(*tls.Config){
		func(c *tls.Config) {
			c.NextProtos = []string{"http/1.1"}
		},
	}

	mgr, err := ctrl.NewManager(
		ctrl.GetConfigOrDie(), ctrl.Options{
			Scheme: scheme,
			Metrics: metricsserver.Options{
				BindAddress: metricsAddr,
				TLSOpts:     tlsOpts,
			},
			HealthProbeBindAddress: probeAddr,
			LeaderElection:         enableLeaderElection,
			LeaderElectionID: "accr-conf-" +
				"inferencing-provider",
		})
	if err != nil {
		setupLog.Error(err,
			"unable to start manager")
		os.Exit(1)
	}

	// Set up the provider reconciler.
	reconciler := provider.NewProviderReconciler(
		mgr.GetClient(), mgr.GetScheme(),
		provider.ProviderDefaults{
			StorageAccountID: os.Getenv(
				"AIRUNWAY_DEFAULT_STORAGE_ACCOUNT_ID"),
			ManagedIdentityID: os.Getenv(
				"AIRUNWAY_DEFAULT_MANAGED_IDENTITY_ID"),
			CvmSku: os.Getenv(
				"AIRUNWAY_DEFAULT_CVM_SKU"),
			EncryptionMode: os.Getenv(
				"AIRUNWAY_DEFAULT_ENCRYPTION_MODE"),
		})
	if err := reconciler.SetupWithManager(
		mgr,
	); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller",
			"AccrConfInferencingProvider")
		os.Exit(1)
	}

	// Set up provider config registration +
	// heartbeat.
	configMgr := provider.NewProviderConfigManager(
		mgr.GetClient())
	if err := mgr.Add(
		manager.RunnableFunc(
			func(ctx context.Context) error {
				setupLog.Info(
					"registering provider config")
				if regErr := configMgr.Register(
					ctx,
				); regErr != nil {
					return regErr
				}
				configMgr.StartHeartbeat(ctx)
				<-ctx.Done()
				setupLog.Info(
					"unregistering provider config")
				return configMgr.Unregister(
					context.Background())
			},
		),
	); err != nil {
		setupLog.Error(err,
			"unable to add provider config runnable")
		os.Exit(1)
	}

	if err := mgr.AddHealthzCheck(
		"healthz", healthz.Ping,
	); err != nil {
		setupLog.Error(err,
			"unable to set up health check")
		os.Exit(1)
	}
	if err := mgr.AddReadyzCheck(
		"readyz", healthz.Ping,
	); err != nil {
		setupLog.Error(err,
			"unable to set up ready check")
		os.Exit(1)
	}

	setupLog.Info(
		"starting accr-conf-inferencing provider")
	if err := mgr.Start(
		ctrl.SetupSignalHandler(),
	); err != nil {
		setupLog.Error(err,
			"problem running manager")
		os.Exit(1)
	}
}
