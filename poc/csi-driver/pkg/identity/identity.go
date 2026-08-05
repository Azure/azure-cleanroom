// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package identity

import (
	"context"
	"fmt"
	"net"
	"time"

	csi "github.com/container-storage-interface/spec/lib/go/csi"
	log "github.com/sirupsen/logrus"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

const (
	driverName    = "cleanroom.csi.azure.com"
	driverVersion = "0.1.0"

	// Sidecar ports must match DaemonSet container port declarations.
	PortIdentity      = 8290
	PortSecrets       = 9300
	PortSKR           = 8284
	PortGovernanceOIDC = 8300

	probeTimeout = 5 * time.Second
)

var sidecarPorts = []int{PortIdentity, PortSecrets, PortSKR}

// Service implements csi.IdentityServer.
type Service struct {
	csi.UnimplementedIdentityServer
}

func NewService() *Service {
	return &Service{}
}

func (s *Service) GetPluginInfo(
	ctx context.Context,
	req *csi.GetPluginInfoRequest,
) (*csi.GetPluginInfoResponse, error) {
	return &csi.GetPluginInfoResponse{
		Name:          driverName,
		VendorVersion: driverVersion,
	}, nil
}

func (s *Service) GetPluginCapabilities(
	ctx context.Context,
	req *csi.GetPluginCapabilitiesRequest,
) (*csi.GetPluginCapabilitiesResponse, error) {
	return &csi.GetPluginCapabilitiesResponse{}, nil
}

// Probe returns OK only when all required sidecars are reachable.
// This prevents kubelet from sending mount requests before sidecars are ready.
func (s *Service) Probe(
	ctx context.Context,
	req *csi.ProbeRequest,
) (*csi.ProbeResponse, error) {
	for _, port := range sidecarPorts {
		addr := fmt.Sprintf("localhost:%d", port)
		conn, err := net.DialTimeout("tcp", addr, probeTimeout)
		if err != nil {
			log.WithFields(log.Fields{
				"port":  port,
				"error": err,
			}).Warn("Sidecar not reachable, reporting NOT_READY")
			return nil, status.Errorf(codes.FailedPrecondition,
				"sidecar on port %d not reachable: %v", port, err)
		}
		_ = conn.Close()
	}
	return &csi.ProbeResponse{}, nil
}
