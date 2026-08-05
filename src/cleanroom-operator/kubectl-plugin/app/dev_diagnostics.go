package app

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"

	"github.com/spf13/cobra"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime/schema"
)

func newDevCollectLogsCmd() *cobra.Command {
	var outputDir string
	var namespace string

	cmd := &cobra.Command{
		Use:   "collect-logs",
		Short: "Collect cluster diagnostics and telemetry",
		Long: `Collect Kubernetes resource dumps, pod logs,
and telemetry from the Aspire dashboard for debugging.

Output includes:
  - CRD resources (environments, clusters, ccfnetworks,
    ccfmembers, ccfusers, modelregistrations,
    governanceservices, governancecontracts,
    workloadgovernances)
  - Pod descriptions and logs
  - Events
  - Aspire telemetry (traces, logs, resources)`,
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runCollectLogs(
				cmd.Context(), outputDir, namespace,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(
		&outputDir, "output-dir", "./cluster-logs",
		"Directory to write collected logs",
	)
	f.StringVar(
		&namespace, "namespace", "",
		"Namespace to collect from "+
			"(default: all namespaces)",
	)
	return cmd
}

func runCollectLogs(
	ctx context.Context,
	outputDir string,
	namespace string,
) error {
	if err := os.MkdirAll(
		outputDir, 0o755,
	); err != nil {
		return fmt.Errorf(
			"creating output dir: %w", err,
		)
	}

	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	clientset, err := getClientset()
	if err != nil {
		return err
	}

	// Collect CRD resources.
	crds := []struct {
		name string
		gvr  schema.GroupVersionResource
	}{
		{"environments", environmentGVR},
		{"clusters", clusterGVR},
		{"ccfnetworks", ccfNetworkGVR},
		{"ccfmembers", ccfMemberGVR},
		{"ccfusers", ccfUserGVR},
		{"modelregistrations", modelRegistrationGVR},
		{"governanceservices", governanceServiceGVR},
		{"governancecontracts", governanceContractGVR},
		{"workloadgovernances", workloadGovernanceGVR},
	}

	for _, crd := range crds {
		fmt.Printf("Collecting %s...\n", crd.name)
		var list interface{}
		var listErr error
		if namespace != "" {
			list, listErr = client.Resource(
				crd.gvr,
			).Namespace(namespace).List(
				ctx, metav1.ListOptions{},
			)
		} else {
			list, listErr = client.Resource(
				crd.gvr,
			).List(ctx, metav1.ListOptions{})
		}
		if listErr != nil {
			fmt.Printf(
				"  Warning: %s: %v\n",
				crd.name, listErr,
			)
			continue
		}
		data, _ := json.MarshalIndent(list, "", "  ")
		path := filepath.Join(
			outputDir, crd.name+".json",
		)
		_ = os.WriteFile(path, data, 0o644)
	}

	// Collect pods.
	fmt.Println("Collecting pods...")
	podNs := namespace
	if podNs == "" {
		podNs = operatorNamespace
	}
	pods, err := clientset.CoreV1().Pods(podNs).List(
		ctx, metav1.ListOptions{},
	)
	if err != nil {
		fmt.Printf("  Warning: pods: %v\n", err)
	} else {
		data, _ := json.MarshalIndent(pods, "", "  ")
		path := filepath.Join(outputDir, "pods.json")
		_ = os.WriteFile(path, data, 0o644)

		// Collect logs for each pod.
		logsDir := filepath.Join(outputDir, "pod-logs")
		_ = os.MkdirAll(logsDir, 0o755)
		for _, pod := range pods.Items {
			for _, c := range pod.Spec.Containers {
				fmt.Printf(
					"  Collecting logs: %s/%s\n",
					pod.Name, c.Name,
				)
				logOpts := logOptions(c.Name)
				logReq := clientset.CoreV1().
					Pods(podNs).GetLogs(
					pod.Name,
					&logOpts,
				)
				logStream, logErr := logReq.Stream(ctx)
				if logErr != nil {
					fmt.Printf(
						"    Warning: %v\n", logErr,
					)
					continue
				}
				logData, _ := io.ReadAll(logStream)
				logStream.Close()
				logFile := filepath.Join(
					logsDir,
					fmt.Sprintf(
						"%s_%s.log",
						pod.Name, c.Name,
					),
				)
				_ = os.WriteFile(
					logFile, logData, 0o644,
				)
			}
		}
	}

	// Collect events.
	fmt.Println("Collecting events...")
	events, err := clientset.CoreV1().Events(
		podNs,
	).List(ctx, metav1.ListOptions{})
	if err != nil {
		fmt.Printf("  Warning: events: %v\n", err)
	} else {
		data, _ := json.MarshalIndent(events, "", "  ")
		path := filepath.Join(outputDir, "events.json")
		_ = os.WriteFile(path, data, 0o644)
	}

	// Collect Aspire telemetry via port-forward.
	fmt.Println("Collecting Aspire telemetry...")
	collectAspireTelemetry(ctx, outputDir, podNs)

	fmt.Printf(
		"\nDiagnostics collected in %s/\n", outputDir,
	)
	return nil
}

func collectAspireTelemetry(
	ctx context.Context,
	outputDir string,
	namespace string,
) {
	clientset, err := getClientset()
	if err != nil {
		fmt.Printf(
			"  Warning: telemetry: %v\n", err,
		)
		return
	}

	// Find aspire-dashboard pod.
	pods, err := clientset.CoreV1().Pods(
		namespace,
	).List(ctx, metav1.ListOptions{
		LabelSelector: "app=aspire-dashboard",
	})
	if err != nil || len(pods.Items) == 0 {
		fmt.Println(
			"  Aspire dashboard not found, skipping",
		)
		return
	}

	pf, localPort, err := startPortForward(
		namespace,
		pods.Items[0].Name,
		18888,
	)
	if err != nil {
		fmt.Printf(
			"  Warning: port-forward: %v\n", err,
		)
		return
	}
	defer pf.Process.Kill()

	baseURL := fmt.Sprintf(
		"http://localhost:%d/api/telemetry", localPort,
	)

	// The Aspire telemetry API wraps OTLP data for
	// traces and logs in { data, totalCount,
	// returnedCount }. Extract .data so the file is
	// directly replayable. The resources endpoint
	// returns a plain array without an envelope.
	type telemetryEndpoint struct {
		name        string
		hasEnvelope bool
	}
	endpoints := []telemetryEndpoint{
		{"resources", false},
		{"logs", true},
		{"traces", true},
	}

	telemetryDir := filepath.Join(
		outputDir, "telemetry",
	)
	_ = os.MkdirAll(telemetryDir, 0o755)

	for _, ep := range endpoints {
		url := baseURL + "/" + ep.name
		if ep.hasEnvelope {
			// Default limit is 200; request all.
			url += "?limit=10000"
		}
		req, err := http.NewRequestWithContext(
			ctx, http.MethodGet, url, nil,
		)
		if err != nil {
			fmt.Printf(
				"  Warning: %s: %v\n", ep.name, err,
			)
			continue
		}
		req.Header.Set("Accept", "application/json")
		resp, err := http.DefaultClient.Do(req)
		if err != nil {
			fmt.Printf(
				"  Warning: %s: %v\n", ep.name, err,
			)
			continue
		}
		body, _ := io.ReadAll(resp.Body)
		resp.Body.Close()

		if resp.StatusCode != http.StatusOK {
			fmt.Printf(
				"  Warning: %s: HTTP %d\n",
				ep.name, resp.StatusCode,
			)
			continue
		}

		var output []byte
		if ep.hasEnvelope {
			// Unwrap { data, totalCount,
			// returnedCount } envelope.
			var envelope struct {
				Data json.RawMessage `json:"data"`
			}
			if err := json.Unmarshal(
				body, &envelope,
			); err != nil {
				fmt.Printf(
					"  Warning: %s parse: %v\n",
					ep.name, err,
				)
				continue
			}
			output = envelope.Data
		} else {
			output = body
		}

		path := filepath.Join(
			telemetryDir, ep.name+".json",
		)
		_ = os.WriteFile(path, output, 0o644)
		fmt.Printf("  Saved %s\n", ep.name)
	}
}

func newDevAspireDashboardCmd() *cobra.Command {
	var port int
	var namespace string

	cmd := &cobra.Command{
		Use:   "aspire-dashboard",
		Short: "Port-forward to the Aspire dashboard",
		Long: `Opens a port-forward to the aspire-dashboard
service for viewing distributed traces and logs.
Press Ctrl+C to stop.`,
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runAspireDashboard(
				cmd.Context(), port, namespace,
			)
		},
	}

	f := cmd.Flags()
	f.IntVar(
		&port, "port", 18888,
		"Local port for the dashboard",
	)
	f.StringVar(
		&namespace, "namespace", operatorNamespace,
		"Namespace of the aspire-dashboard",
	)
	return cmd
}

func runAspireDashboard(
	ctx context.Context,
	port int,
	namespace string,
) error {
	clientset, err := getClientset()
	if err != nil {
		return err
	}

	pods, err := clientset.CoreV1().Pods(
		namespace,
	).List(ctx, metav1.ListOptions{
		LabelSelector: "app=aspire-dashboard",
	})
	if err != nil {
		return fmt.Errorf(
			"listing aspire-dashboard pods: %w", err,
		)
	}
	if len(pods.Items) == 0 {
		return fmt.Errorf(
			"no aspire-dashboard pods found in %s",
			namespace,
		)
	}

	pf, localPort, err := startPortForward(
		namespace,
		pods.Items[0].Name,
		port,
	)
	if err != nil {
		return fmt.Errorf(
			"port-forward: %w", err,
		)
	}
	defer pf.Process.Kill()

	fmt.Printf(
		"Aspire dashboard: http://localhost:%d\n",
		localPort,
	)
	fmt.Println("Press Ctrl+C to stop")

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	<-sigCh

	return nil
}
