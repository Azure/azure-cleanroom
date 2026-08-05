// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package main

import (
	_ "embed"
	"encoding/base64"
	"encoding/json"
	"flag"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	log "github.com/sirupsen/logrus"
)

const (
	defaultSocketPath = "/run/blobfuse-proxy/blobfuse-proxy.sock"
	idTokenEndpoint   = "http://localhost:8300"
)

var (
	identityEndpoint string
	secretsEndpoint  string
)

func blobfuse2BinaryPath() string {
	if p := os.Getenv("BLOBFUSE2_BINARY"); p != "" {
		return p
	}
	return "/opt/cleanroom/bin/blobfuse2"
}

//go:embed encryptor-config.yaml
var encryptorConfigTemplate []byte

func main() {
	if len(os.Args) > 1 && os.Args[1] == "mount-all" {
		runMountAll(os.Args[2:])
		return
	}
	runServe(os.Args[1:])
}

func runServe(args []string) {
	fs := flag.NewFlagSet("serve", flag.ExitOnError)
	socketPath := fs.String("socket", defaultSocketPath, "Unix socket path")
	identityEP := fs.String("identity-endpoint", "http://localhost:8290", "Identity sidecar base URL")
	secretsEP := fs.String("secrets-endpoint", "http://localhost:9300", "Secrets sidecar base URL")
	fs.Parse(args) // ExitOnError: never returns non-nil; return value intentionally ignored.

	identityEndpoint = *identityEP
	secretsEndpoint = *secretsEP

	log.SetFormatter(&log.TextFormatter{FullTimestamp: true})
	log.SetLevel(log.InfoLevel)

	if err := os.MkdirAll(filepath.Dir(*socketPath), 0750); err != nil {
		log.WithError(err).Fatal("Failed to create socket directory")
	}

	_ = os.Remove(*socketPath)

	listener, err := net.Listen("unix", *socketPath)
	if err != nil {
		log.WithError(err).Fatal("Failed to listen on socket")
	}
	defer func() { _ = listener.Close() }()

	if err := os.Chmod(*socketPath, 0660); err != nil {
		log.WithError(err).Warn("Failed to set socket permissions")
	}

	log.WithField("socket", *socketPath).Info("blobfuse-proxy listening")

	for {
		conn, err := listener.Accept()
		if err != nil {
			log.WithError(err).Error("Accept error")
			continue
		}
		go handleConn(conn)
	}
}

func volumeStatusPath() string {
	if p := os.Getenv("VOLUMESTATUS_MOUNT_PATH"); p != "" {
		return p
	}
	return "/mnt/volumestatus"
}

func writeVolumeReady(mountPath string) {
	base := volumeStatusPath()
	if err := os.MkdirAll(base, 0o755); err != nil {
		log.WithError(err).WithField("dir", base).Warn("Failed to create volumestatus dir")
		return
	}
	accessName := filepath.Base(mountPath)
	marker := filepath.Join(base, accessName+".volume.ready")
	data, _ := json.Marshal(map[string]string{"mount_path": mountPath})
	if err := os.WriteFile(marker, data, 0o644); err != nil {
		log.WithError(err).WithField("marker", marker).Warn("Failed to write volume ready marker")
	}
}

func writeVolumeError(mountPath string) {
	base := volumeStatusPath()
	_ = os.MkdirAll(base, 0o755)
	accessName := filepath.Base(mountPath)
	marker := filepath.Join(base, accessName+".volume.error")
	data, _ := json.Marshal(map[string]int{"error_code": 1})
	_ = os.WriteFile(marker, data, 0o644)
}

func waitForEndpoint(url string, timeout time.Duration) {
	deadline := time.Now().Add(timeout)
	client := &http.Client{Timeout: 2 * time.Second}
	for time.Now().Before(deadline) {
		if _, err := client.Get(url); err == nil {
			log.WithField("url", url).Info("Endpoint is reachable")
			return
		}
		log.WithField("url", url).Debug("Waiting for endpoint...")
		time.Sleep(1 * time.Second)
	}
	log.WithField("url", url).Warn("Endpoint did not become reachable within timeout; proceeding anyway")
}

func runMountAll(args []string) {
	fs := flag.NewFlagSet("mount-all", flag.ExitOnError)
	identityEP := fs.String("identity-endpoint", "http://localhost:8290", "Identity sidecar base URL")
	secretsEP := fs.String("secrets-endpoint", "http://localhost:9300", "Secrets sidecar base URL")
	fs.Parse(args) // ExitOnError: never returns non-nil; return value intentionally ignored.

	identityEndpoint = *identityEP
	secretsEndpoint = *secretsEP

	log.SetFormatter(&log.TextFormatter{FullTimestamp: true})
	log.SetLevel(log.InfoLevel)

	encoded := os.Getenv("BLOBFUSE_MOUNTS_JSON")
	if encoded == "" {
		log.Fatal("BLOBFUSE_MOUNTS_JSON env var not set")
	}

	data, err := base64.StdEncoding.DecodeString(encoded)
	if err != nil {
		log.WithError(err).Fatal("Failed to base64-decode BLOBFUSE_MOUNTS_JSON")
	}

	var mounts []MountRequest
	if err := json.Unmarshal(data, &mounts); err != nil {
		log.WithError(err).Fatal("Failed to parse BLOBFUSE_MOUNTS_JSON")
	}

	log.WithField("count", len(mounts)).Info("Starting mount-all")
	waitForEndpoint(identityEndpoint+"/healthz", 120*time.Second)
	waitForEndpoint(secretsEndpoint+"/healthz", 120*time.Second)

	for i := range mounts {
		m := &mounts[i]
		log.WithFields(log.Fields{"index": i, "mountPath": m.MountPath}).Info("Mounting")
		if err := doMount(m); err != nil {
			writeVolumeError(m.MountPath)
			log.WithError(err).WithField("mountPath", m.MountPath).Fatal("Mount failed, exiting")
		}
		writeVolumeReady(m.MountPath)
		log.WithField("mountPath", m.MountPath).Info("Volume ready marker written")
	}

	log.Info("All mounts successful, blocking")
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, syscall.SIGTERM, syscall.SIGINT)
	s := <-sig
	log.WithField("signal", s).Info("Received signal, exiting")
}
