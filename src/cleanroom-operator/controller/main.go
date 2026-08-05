package main

import (
	"context"
	"flag"
	"fmt"
	"os"

	"k8s.io/apimachinery/pkg/runtime"
	utilruntime "k8s.io/apimachinery/pkg/util/runtime"
	clientgoscheme "k8s.io/client-go/kubernetes/scheme"
	ctrl "sigs.k8s.io/controller-runtime"
	"sigs.k8s.io/controller-runtime/pkg/healthz"
	"sigs.k8s.io/controller-runtime/pkg/log/zap"

	"go.opentelemetry.io/contrib/bridges/otelzap"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/exporters/otlp/otlplog/otlploggrpc"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/propagation"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.26.0"
	uzap "go.uber.org/zap"
	"go.uber.org/zap/zapcore"

	v1alpha1 "github.com/Azure/azure-cleanroom/cleanroom-operator/api/v1alpha1"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/client"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/internal/azure"
	"github.com/Azure/azure-cleanroom/cleanroom-operator/internal/controller"
)

var (
	scheme   = runtime.NewScheme()
	setupLog = ctrl.Log.WithName("setup")
)

func init() {
	utilruntime.Must(clientgoscheme.AddToScheme(scheme))
	utilruntime.Must(v1alpha1.AddToScheme(scheme))
}

func main() {
	var metricsAddr string
	var probeAddr string
	var clusterProviderEndpoint string
	var ccfProviderEndpoint string

	flag.StringVar(&metricsAddr, "metrics-bind-address",
		":8080", "The address the metric endpoint binds to.")
	flag.StringVar(&probeAddr, "health-probe-bind-address",
		":8081", "The address the probe endpoint binds to.")
	flag.StringVar(&clusterProviderEndpoint,
		"cluster-provider-endpoint", "",
		"The cluster-provider-client endpoint URL.")
	flag.StringVar(&ccfProviderEndpoint,
		"ccf-provider-endpoint", "",
		"The ccf-provider-client endpoint URL.")

	opts := zap.Options{Development: true}
	opts.BindFlags(flag.CommandLine)
	flag.Parse()

	// Build logger options; conditionally add OTEL
	// bridge if OTLP endpoint is configured.
	loggerOpts := []zap.Opts{zap.UseFlagOptions(&opts)}

	otlpEndpoint := os.Getenv(
		"OTEL_EXPORTER_OTLP_ENDPOINT",
	)
	if otlpEndpoint != "" {
		shutdown, err := initTracer(otlpEndpoint)
		if err != nil {
			fmt.Fprintf(
				os.Stderr,
				"OTLP tracer init failed: %v\n",
				err,
			)
		} else {
			defer shutdown()
		}

		lp, lpShutdown, err := initLogProvider(
			otlpEndpoint,
		)
		if err != nil {
			fmt.Fprintf(
				os.Stderr,
				"OTLP log provider init failed: %v\n",
				err,
			)
		} else {
			defer lpShutdown()
			otelCore := otelzap.NewCore(
				"cleanroom-operator",
				otelzap.WithLoggerProvider(lp),
			)
			loggerOpts = append(loggerOpts,
				zap.RawZapOpts(
					uzap.WrapCore(
						func(c zapcore.Core) zapcore.Core {
							return &contextTeeCore{
								console: c,
								otel:    otelCore,
							}
						},
					),
				),
			)
		}
	}

	ctrl.SetLogger(zap.New(loggerOpts...))

	if otlpEndpoint != "" {
		setupLog.Info(
			"OTLP tracing and logging enabled",
			"endpoint", otlpEndpoint,
		)
	}

	if clusterProviderEndpoint == "" {
		clusterProviderEndpoint = os.Getenv(
			"CLUSTER_PROVIDER_CLIENT_ENDPOINT",
		)
	}
	if clusterProviderEndpoint == "" {
		setupLog.Error(nil,
			"cluster-provider-endpoint or "+
				"CLUSTER_PROVIDER_CLIENT_ENDPOINT must be set")
		os.Exit(1)
	}

	if ccfProviderEndpoint == "" {
		ccfProviderEndpoint = os.Getenv(
			"CCF_PROVIDER_CLIENT_ENDPOINT",
		)
	}

	mgr, err := ctrl.NewManager(ctrl.GetConfigOrDie(), ctrl.Options{
		Scheme:                 scheme,
		HealthProbeBindAddress: probeAddr,
	})
	if err != nil {
		setupLog.Error(err, "unable to create manager")
		os.Exit(1)
	}

	clusterClient := client.NewClusterClient(clusterProviderEndpoint)

	if err := (&controller.ClusterReconciler{
		Client:        mgr.GetClient(),
		Scheme:        mgr.GetScheme(),
		ClusterClient: clusterClient,
		Recorder:      mgr.GetEventRecorderFor("cluster-controller"),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "Cluster")
		os.Exit(1)
	}

	cgsClient := client.NewCgsClient()

	// Create Azure SDK client for model deployment
	// operations (OIDC + access setup). Uses
	// DefaultAzureCredential (workload identity in-cluster).
	azureClient, err := azure.NewClient()
	if err != nil {
		setupLog.Error(err,
			"unable to create Azure client "+
				"(model deployment features disabled)")
	}

	// Create CCF provider client if endpoint is configured.
	// Used by both CcfNetwork and GovernanceContract
	// controllers.
	var ccfClient *client.CcfNetworkClient
	if ccfProviderEndpoint != "" {
		ccfClient = client.NewCcfNetworkClient(
			ccfProviderEndpoint,
		)
	}

	if err := (&controller.CcfMemberReconciler{
		Client:    mgr.GetClient(),
		Scheme:    mgr.GetScheme(),
		CgsClient: cgsClient,
		Recorder: mgr.GetEventRecorderFor(
			"ccfmember-controller",
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "CcfMember")
		os.Exit(1)
	}

	if err := (&controller.GovernanceContractReconciler{
		Client:           mgr.GetClient(),
		Scheme:           mgr.GetScheme(),
		CgsClient:        cgsClient,
		CcfNetworkClient: ccfClient,
		Recorder: mgr.GetEventRecorderFor(
			"governancecontract-controller",
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "GovernanceContract")
		os.Exit(1)
	}

	if err := (&controller.GovernanceServiceReconciler{
		Client:      mgr.GetClient(),
		Scheme:      mgr.GetScheme(),
		CgsClient:   cgsClient,
		AzureClient: azureClient,
		Recorder: mgr.GetEventRecorderFor(
			"governanceservice-controller",
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "GovernanceService")
		os.Exit(1)
	}

	if err := (&controller.EnvironmentReconciler{
		Client: mgr.GetClient(),
		Scheme: mgr.GetScheme(),
		Recorder: mgr.GetEventRecorderFor(
			"environment-controller",
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "Environment")
		os.Exit(1)
	}

	if err := (&controller.WorkloadGovernanceReconciler{
		Client:        mgr.GetClient(),
		Scheme:        mgr.GetScheme(),
		CgsClient:     cgsClient,
		ClusterClient: clusterClient,
		Recorder: mgr.GetEventRecorderFor(
			"workloadgovernance-controller",
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "WorkloadGovernance")
		os.Exit(1)
	}

	if err := (&controller.ModelRegistrationReconciler{
		Client:      mgr.GetClient(),
		Scheme:      mgr.GetScheme(),
		CgsClient:   cgsClient,
		AzureClient: azureClient,
		Recorder: mgr.GetEventRecorderFor(
			"modelregistration-controller",
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "ModelGovernance")
		os.Exit(1)
	}

	if err := (&controller.ModelDeploymentReconciler{
		Client: mgr.GetClient(),
		Scheme: mgr.GetScheme(),
		Recorder: mgr.GetEventRecorderFor(
			"modeldeployment-controller",
		),
		CgsClient: cgsClient,
		InferencingClientFactory: client.NewInferencingClientFactory(
			clusterClient,
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller",
			"ModelDeployment")
		os.Exit(1)
	}

	if err := (&controller.CcfUserReconciler{
		Client:    mgr.GetClient(),
		Scheme:    mgr.GetScheme(),
		CgsClient: cgsClient,
		Recorder: mgr.GetEventRecorderFor(
			"ccfuser-controller",
		),
	}).SetupWithManager(mgr); err != nil {
		setupLog.Error(err,
			"unable to create controller",
			"controller", "CcfUser")
		os.Exit(1)
	}

	if ccfClient != nil {
		if err := (&controller.CcfNetworkReconciler{
			Client:           mgr.GetClient(),
			Scheme:           mgr.GetScheme(),
			CcfNetworkClient: ccfClient,
			Recorder: mgr.GetEventRecorderFor(
				"ccfnetwork-controller",
			),
		}).SetupWithManager(mgr); err != nil {
			setupLog.Error(err,
				"unable to create controller",
				"controller", "CcfNetwork")
			os.Exit(1)
		}
	}

	if err := mgr.AddHealthzCheck(
		"healthz", healthz.Ping,
	); err != nil {
		setupLog.Error(err, "unable to set up health check")
		os.Exit(1)
	}
	if err := mgr.AddReadyzCheck(
		"readyz", healthz.Ping,
	); err != nil {
		setupLog.Error(err, "unable to set up ready check")
		os.Exit(1)
	}

	setupLog.Info("starting manager",
		"clusterProviderEndpoint", clusterProviderEndpoint,
		"ccfProviderEndpoint", ccfProviderEndpoint)
	if err := mgr.Start(ctrl.SetupSignalHandler()); err != nil {
		setupLog.Error(err, "problem running manager")
		os.Exit(1)
	}
}

// stripScheme removes the http:// or https:// prefix
// from an endpoint. gRPC exporters expect host:port only.
func stripScheme(endpoint string) string {
	for _, prefix := range []string{
		"http://", "https://",
	} {
		if len(endpoint) > len(prefix) &&
			endpoint[:len(prefix)] == prefix {
			return endpoint[len(prefix):]
		}
	}
	return endpoint
}

func otelResource() (*resource.Resource, error) {
	return resource.New(
		context.Background(),
		resource.WithAttributes(
			semconv.ServiceName("cleanroom-operator"),
		),
	)
}

func initTracer(
	endpoint string,
) (func(), error) {
	ctx := context.Background()
	host := stripScheme(endpoint)

	exporter, err := otlptracegrpc.New(
		ctx,
		otlptracegrpc.WithEndpoint(host),
		otlptracegrpc.WithInsecure(),
	)
	if err != nil {
		return nil, err
	}

	res, err := otelResource()
	if err != nil {
		return nil, err
	}

	tp := sdktrace.NewTracerProvider(
		sdktrace.WithBatcher(exporter),
		sdktrace.WithResource(res),
	)

	otel.SetTracerProvider(tp)
	otel.SetTextMapPropagator(
		propagation.NewCompositeTextMapPropagator(
			propagation.TraceContext{},
			propagation.Baggage{},
		),
	)

	return func() {
		_ = tp.Shutdown(context.Background())
	}, nil
}

func initLogProvider(
	endpoint string,
) (*sdklog.LoggerProvider, func(), error) {
	ctx := context.Background()
	host := stripScheme(endpoint)

	exporter, err := otlploggrpc.New(
		ctx,
		otlploggrpc.WithEndpoint(host),
		otlploggrpc.WithInsecure(),
	)
	if err != nil {
		return nil, nil, err
	}

	res, err := otelResource()
	if err != nil {
		return nil, nil, err
	}

	lp := sdklog.NewLoggerProvider(
		sdklog.WithProcessor(
			sdklog.NewBatchProcessor(exporter),
		),
		sdklog.WithResource(res),
	)

	return lp, func() {
		_ = lp.Shutdown(context.Background())
	}, nil
}

// contextTeeCore tees log entries to a console core and an
// OTel core. It intercepts context.Context values in zap
// fields (injected via logr.WithValues) to enable trace
// correlation through the otelzap bridge, while stripping
// them from console output to avoid serialization noise.
type contextTeeCore struct {
	console zapcore.Core
	otel    zapcore.Core
	ctx     context.Context
}

func (c *contextTeeCore) Enabled(
	lvl zapcore.Level,
) bool {
	return c.console.Enabled(lvl) ||
		c.otel.Enabled(lvl)
}

func (c *contextTeeCore) With(
	fields []zapcore.Field,
) zapcore.Core {
	var ctx context.Context
	clean := make([]zapcore.Field, 0, len(fields))
	for _, f := range fields {
		if v, ok := f.Interface.(context.Context); ok {
			ctx = v
			continue
		}
		clean = append(clean, f)
	}
	newCtx := c.ctx
	if ctx != nil {
		newCtx = ctx
	}
	return &contextTeeCore{
		console: c.console.With(clean),
		otel:    c.otel.With(clean),
		ctx:     newCtx,
	}
}

func (c *contextTeeCore) Check(
	ent zapcore.Entry,
	ce *zapcore.CheckedEntry,
) *zapcore.CheckedEntry {
	if c.Enabled(ent.Level) {
		return ce.AddCore(ent, c)
	}
	return ce
}

func (c *contextTeeCore) Write(
	ent zapcore.Entry,
	fields []zapcore.Field,
) error {
	var ctx context.Context
	clean := make([]zapcore.Field, 0, len(fields))
	for _, f := range fields {
		if v, ok := f.Interface.(context.Context); ok {
			ctx = v
			continue
		}
		clean = append(clean, f)
	}
	if ctx == nil {
		ctx = c.ctx
	}

	// Console gets clean fields only.
	if err := c.console.Write(ent, clean); err != nil {
		return err
	}

	// OTel core gets clean fields plus the context for
	// trace/span ID correlation.
	otelFields := clean
	if ctx != nil {
		otelFields = append(
			otelFields,
			zapcore.Field{Interface: ctx},
		)
	}
	return c.otel.Write(ent, otelFields)
}

func (c *contextTeeCore) Sync() error {
	cErr := c.console.Sync()
	oErr := c.otel.Sync()
	if cErr != nil {
		return cErr
	}
	return oErr
}
