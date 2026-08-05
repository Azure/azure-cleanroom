// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package server

import (
	"fmt"

	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/components/azcli"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/components/configure"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/config"
	"github.com/azure/azure-cleanroom/src/tools/cleanroom-mcp/internal/logger"
	"github.com/mark3labs/mcp-go/server"
)

const version = "0.2.0"

// Service represents the cleanroom MCP service.
type Service struct {
	cfg       *config.ConfigData
	mcpServer *server.MCPServer
}

// NewService creates a new cleanroom MCP service.
func NewService(cfg *config.ConfigData) *Service {
	return &Service{cfg: cfg}
}

// Initialize sets up the MCP server and registers all tools.
func (s *Service) Initialize() error {
	logger.Infof("Initializing Cleanroom MCP service...")

	s.mcpServer = server.NewMCPServer(
		"Cleanroom MCP",
		version,
		server.WithResourceCapabilities(true, true),
		server.WithLogging(),
		server.WithRecovery(),
	)

	s.registerComponents()

	logger.Infof("Cleanroom MCP service initialization completed successfully")
	return nil
}

// Run starts the service with the configured transport.
func (s *Service) Run() error {
	logger.Infof("Cleanroom MCP version: %s", version)

	switch s.cfg.Transport {
	case "stdio":
		logger.Infof("Listening for requests on STDIO...")
		return server.ServeStdio(s.mcpServer)
	case "sse":
		addr := fmt.Sprintf("%s:%d", s.cfg.Host, s.cfg.Port)
		sse := server.NewSSEServer(s.mcpServer)
		logger.Infof("SSE server listening on %s", addr)
		return sse.Start(addr)
	case "streamable-http":
		addr := fmt.Sprintf("%s:%d", s.cfg.Host, s.cfg.Port)
		streamable := server.NewStreamableHTTPServer(s.mcpServer)
		logger.Infof("Streamable HTTP server listening on %s", addr)
		return streamable.Start(addr)
	default:
		return fmt.Errorf(
			"invalid transport type: %s (must be 'stdio', 'sse' or 'streamable-http')",
			s.cfg.Transport)
	}
}

func (s *Service) registerComponents() {
	logger.Infof("Registering components...")

	// Configuration tool for runtime client name management.
	logger.Infof("Registering configuration tool: cleanroom_configure")
	cfgTool := configure.RegisterConfigureTool()
	cfgHandler := configure.NewConfigureHandler(s.cfg)
	s.mcpServer.AddTool(cfgTool, cfgHandler)

	// Unified az cleanroom CLI tool.
	logger.Infof("Registering unified tool: call_az_cleanroom")
	tool := azcli.RegisterCallAzCleanroomTool()
	handler := azcli.NewCallAzCleanroomHandler(s.cfg)
	s.mcpServer.AddTool(tool, handler)

	logger.Infof("All components registered successfully")
}
