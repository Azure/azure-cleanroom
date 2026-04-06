package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"syscall"

	"github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy/internal/apiserverproxy"
	"github.com/azure/azure-cleanroom/src/k8s-node/api-server-proxy/internal/apiserverproxy/admission"
)

func main() {
	os.Exit(run())
}

func run() int {
	cfg := &apiserverproxy.Config{}

	flag.StringVar(&cfg.KubeconfigPath, "kubeconfig", "", "Path to kubeconfig file for API server connection (required)")
	flag.StringVar(&cfg.KubeconfigContext, "context", "", "Context to use from kubeconfig (optional, uses current-context if empty)")
	flag.StringVar(&cfg.ListenAddr, "listen-addr", ":6443", "Address to listen on for kubelet connections")
	flag.StringVar(&cfg.TLSCertFile, "tls-cert", "", "Path to TLS certificate file for serving")
	flag.StringVar(&cfg.TLSKeyFile, "tls-key", "", "Path to TLS key file for serving")
	flag.StringVar(&cfg.PolicyVerificationCert, "policy-verification-cert", "", "Path to public key certificate for verifying pod policy signatures (optional)")
	flag.BoolVar(&cfg.LogRequests, "log-requests", true, "Log all proxied requests")
	flag.BoolVar(&cfg.LogPodPayloads, "log-pod-payloads", false, "Log full pod payloads for pod creation requests")
	flag.Parse()

	if cfg.KubeconfigPath == "" {
		fmt.Fprintln(os.Stderr, "Error: --kubeconfig is required")
		flag.Usage()
		return 1
	}

	// Load kubeconfig
	if err := cfg.LoadKubeconfigFromFile(); err != nil {
		fmt.Fprintf(os.Stderr, "Error loading kubeconfig: %v\n", err)
		return 1
	}

	// Create admission controller
	var controllers []admission.Controller

	// Add pod policy verification controller if configured
	if cfg.PolicyVerificationCert != "" {
		policyController, err := admission.NewPolicyVerificationController(cfg.PolicyVerificationCert)
		if err != nil {
			fmt.Fprintf(os.Stderr, "Error loading policy verification cert: %v\n", err)
			return 1
		}
		controllers = append(controllers, policyController)
	}

	// Build the final admission controller
	var admissionController admission.Controller
	if len(controllers) == 0 {
		// Default to allowing all pods but logging them
		admissionController = admission.NewLoggingController()
	} else if len(controllers) == 1 {
		admissionController = controllers[0]
	} else {
		// Chain multiple controllers together
		admissionController = admission.NewChainController(controllers...)
	}

	// Create and configure proxy
	proxy, err := apiserverproxy.New(cfg, admissionController)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error creating proxy: %v\n", err)
		return 1
	}

	// Setup signal handling for graceful shutdown
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	go func() {
		sig := <-sigCh
		fmt.Printf("\nReceived signal %v, shutting down...\n", sig)
		cancel()
	}()

	// Start proxy
	fmt.Printf("Starting api-server-proxy...\n")
	fmt.Printf("  Listening on: %s\n", cfg.ListenAddr)
	fmt.Printf("  API Server: %s\n", cfg.LoadedKubeConfig.Server)
	fmt.Printf("  TLS: %v\n", cfg.TLSCertFile != "")
	fmt.Printf("  Pod Policy Verification: %v\n", cfg.PolicyVerificationCert != "")
	fmt.Printf("  Log Requests: %v\n", cfg.LogRequests)
	fmt.Printf("  Log Pod Payloads: %v\n", cfg.LogPodPayloads)

	if err := proxy.Run(ctx); err != nil {
		fmt.Fprintf(os.Stderr, "Error running proxy: %v\n", err)
		return 1
	}

	return 0
}
