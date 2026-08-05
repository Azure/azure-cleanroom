// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"syscall"

	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/config"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/logger"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/server"
)

func main() {
	cfg := config.NewConfig()
	cfg.ParseFlags()

	if err := logger.SetLevel(cfg.LogLevel); err != nil {
		fmt.Fprintf(os.Stderr, "Invalid log level '%s': %v\n", cfg.LogLevel, err)
		os.Exit(1)
	}

	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()

	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)

	service := server.NewService(cfg)
	if err := service.Initialize(); err != nil {
		fmt.Fprintf(os.Stderr, "Initialization error: %v\n", err)
		os.Exit(1)
	}

	errChan := make(chan error, 1)
	go func() {
		errChan <- service.Run()
	}()

	select {
	case <-sigChan:
		cancel()
	case err := <-errChan:
		if err != nil {
			logger.Errorf("Service error: %v", err)
			os.Exit(1)
		}
	case <-ctx.Done():
	}
}
