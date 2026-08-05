package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"syscall"

	"github.com/azure/azure-cleanroom/src/k8s-node/kubelet-proxy/internal/kubeletproxy"
)

func main() {
	os.Exit(run())
}

func run() int {
	cfg := &kubeletproxy.Config{}

	flag.StringVar(&cfg.KubeletURL, "kubelet-url", "",
		"URL of the kubelet backend (required, e.g. https://127.0.0.1:10251)")
	flag.StringVar(&cfg.ListenAddr, "listen-addr", ":10250",
		"Address to listen on for API server connections")
	flag.StringVar(&cfg.ServerCertFile, "server-cert", "",
		"Path to TLS certificate for serving API server requests (required)")
	flag.StringVar(&cfg.ServerKeyFile, "server-key", "",
		"Path to TLS key for serving API server requests (required)")
	flag.StringVar(&cfg.ClientCertFile, "client-cert", "",
		"Path to TLS client certificate for connecting to kubelet (required)")
	flag.StringVar(&cfg.ClientKeyFile, "client-key", "",
		"Path to TLS client key for connecting to kubelet (required)")
	flag.StringVar(&cfg.APIPolicyFile, "api-policy", "",
		"Path to JSON file listing allowed kubelet APIs (optional, uses built-in defaults if empty)")
	flag.BoolVar(&cfg.LogRequests, "log-requests", true,
		"Log all proxied requests")
	flag.Parse()

	if err := cfg.Validate(); err != nil {
		fmt.Fprintf(os.Stderr, "Error: %v\n", err)
		flag.Usage()
		return 1
	}

	// Setup signal handling for graceful shutdown.
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	go func() {
		sig := <-sigCh
		fmt.Printf("\nReceived signal %v, shutting down...\n", sig)
		cancel()
	}()

	proxy, err := kubeletproxy.New(ctx, cfg, nil)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error creating proxy: %v\n", err)
		return 1
	}

	fmt.Printf("Starting kubelet-proxy...\n")
	fmt.Printf("  Listening on: %s\n", cfg.ListenAddr)
	fmt.Printf("  Kubelet URL:  %s\n", cfg.KubeletURL)
	fmt.Printf("  Server cert:  %s\n", cfg.ServerCertFile)
	fmt.Printf("  Client cert:  %s\n", cfg.ClientCertFile)
	fmt.Printf("  API policy:   %s\n", cfg.APIPolicyFile)
	fmt.Printf("  Log requests: %v\n", cfg.LogRequests)

	if err := proxy.Run(ctx); err != nil {
		fmt.Fprintf(os.Stderr, "Error running proxy: %v\n", err)
		return 1
	}

	return 0
}
