// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package main

import (
	"flag"
	"net"
	"os"
	"os/signal"
	"strings"
	"syscall"

	csi "github.com/container-storage-interface/spec/lib/go/csi"
	log "github.com/sirupsen/logrus"
	"google.golang.org/grpc"

	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/blobfuse"
	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/identity"
	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/node"
)

var (
	nodeID     = flag.String("node-id", "", "Node ID reported to kubelet")
	endpoint   = flag.String("endpoint", "unix:///csi/csi.sock", "CSI gRPC endpoint")
	stagingDir = flag.String("staging-dir", "/var/lib/csi/cleanroom/staging", "Driver-private staging directory for blobfuse mounts")
)

func main() {
	flag.Parse()

	log.SetFormatter(&log.TextFormatter{FullTimestamp: true})
	log.SetLevel(log.InfoLevel)

	if *nodeID == "" {
		log.Fatal("--node-id is required")
	}

	manager := blobfuse.NewManager(*stagingDir, "")

	// Recover any existing mounts from a previous run.
	if err := manager.RecoverMounts(); err != nil {
		log.WithError(err).Warn("Mount recovery encountered errors")
	}

	identitySvc := identity.NewService()
	nodeSvc := node.NewService(*nodeID, manager)

	socketPath := strings.TrimPrefix(*endpoint, "unix://")

	// Remove stale socket file if it exists.
	if err := os.Remove(socketPath); err != nil && !os.IsNotExist(err) {
		log.WithError(err).Fatal("Failed to remove existing socket")
	}

	listener, err := net.Listen("unix", socketPath)
	if err != nil {
		log.WithError(err).Fatal("Failed to listen on CSI socket")
	}

	server := grpc.NewServer()
	csi.RegisterIdentityServer(server, identitySvc)
	csi.RegisterNodeServer(server, nodeSvc)

	// Handle SIGTERM: stop the gRPC server. Blobfuse mounts are managed by
	// the blobfuse-proxy host service and remain alive across pod restarts.
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGTERM, syscall.SIGINT)
	go func() {
		sig := <-sigCh
		log.WithField("signal", sig).Info("Received signal, shutting down")
		server.GracefulStop()
		os.Exit(0)
	}()

	log.WithFields(log.Fields{
		"nodeID":   *nodeID,
		"endpoint": *endpoint,
		"staging":  *stagingDir,
	}).Info("Starting Clean Room CSI driver")

	if err := server.Serve(listener); err != nil {
		log.WithError(err).Fatal("gRPC server failed")
	}
}
