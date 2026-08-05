// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package node

import (
	"context"

	csi "github.com/container-storage-interface/spec/lib/go/csi"
	log "github.com/sirupsen/logrus"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/blobfuse"
	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/contracts"
)

// Service implements csi.NodeServer.
type Service struct {
	csi.UnimplementedNodeServer
	nodeID  string
	manager *blobfuse.Manager
}

func NewService(nodeID string, manager *blobfuse.Manager) *Service {
	return &Service{
		nodeID:  nodeID,
		manager: manager,
	}
}

func (s *Service) NodeGetCapabilities(
	ctx context.Context,
	req *csi.NodeGetCapabilitiesRequest,
) (*csi.NodeGetCapabilitiesResponse, error) {
	// Return empty capabilities: this driver uses ephemeral inline volumes only.
	// NodeStageVolume / NodeUnstageVolume are not used; kubelet calls
	// NodePublishVolume directly, so STAGE_UNSTAGE_VOLUME capability must not be advertised.
	return &csi.NodeGetCapabilitiesResponse{}, nil
}

func (s *Service) NodeGetInfo(
	ctx context.Context,
	req *csi.NodeGetInfoRequest,
) (*csi.NodeGetInfoResponse, error) {
	return &csi.NodeGetInfoResponse{
		NodeId: s.nodeID,
	}, nil
}

// NodePublishVolume is called by kubelet to mount a volume into a pod.
// For ephemeral inline volumes, kubelet skips NodeStageVolume and calls this directly.
func (s *Service) NodePublishVolume(
	ctx context.Context,
	req *csi.NodePublishVolumeRequest,
) (*csi.NodePublishVolumeResponse, error) {
	if req.GetVolumeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "volume ID is required")
	}
	if req.GetTargetPath() == "" {
		return nil, status.Error(codes.InvalidArgument, "target path is required")
	}

	attrs := req.GetVolumeContext()
	if attrs == nil {
		return nil, status.Error(codes.InvalidArgument, "volumeAttributes are required")
	}

	config, err := configFromAttributes(attrs, req.GetReadonly())
	if err != nil {
		return nil, status.Errorf(codes.InvalidArgument, "invalid volumeAttributes: %v", err)
	}

	logger := log.WithFields(log.Fields{
		"volumeId":   req.GetVolumeId(),
		"targetPath": req.GetTargetPath(),
	})

	// Log pod info if available (podInfoOnMount: true in CSIDriver resource).
	if podName, ok := attrs["csi.storage.k8s.io/pod.name"]; ok {
		logger = logger.WithField("pod", podName)
	}
	if podNs, ok := attrs["csi.storage.k8s.io/pod.namespace"]; ok {
		logger = logger.WithField("namespace", podNs)
	}

	logger.Info("NodePublishVolume called")

	// Stage: ensure blobfuse2 mount exists (ref-counted), then use the returned
	// mountID for all subsequent manager calls.
	mountID, err := s.manager.Stage(config, req.GetTargetPath())
	if err != nil {
		logger.WithError(err).Error("Stage failed")
		return nil, status.Errorf(codes.Internal, "stage failed: %v", err)
	}
	logger = logger.WithField("mountID", mountID)

	// Publish: bind mount staging dir → pod target path.
	if err := s.manager.Publish(mountID, req.GetTargetPath()); err != nil {
		logger.WithError(err).Error("Publish failed")
		// Unstage to decrement the refcount we just incremented.
		_ = s.manager.Unstage(mountID)
		return nil, status.Errorf(codes.Internal, "publish failed: %v", err)
	}

	logger.Info("NodePublishVolume succeeded")
	return &csi.NodePublishVolumeResponse{}, nil
}

// NodeUnpublishVolume is called by kubelet to unmount a volume from a pod.
func (s *Service) NodeUnpublishVolume(
	ctx context.Context,
	req *csi.NodeUnpublishVolumeRequest,
) (*csi.NodeUnpublishVolumeResponse, error) {
	if req.GetVolumeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "volume ID is required")
	}
	if req.GetTargetPath() == "" {
		return nil, status.Error(codes.InvalidArgument, "target path is required")
	}

	logger := log.WithFields(log.Fields{
		"volumeId":   req.GetVolumeId(),
		"targetPath": req.GetTargetPath(),
	})

	logger.Info("NodeUnpublishVolume called")

	// Unpublish: remove bind mount, get the mount key.
	key, err := s.manager.Unpublish(req.GetTargetPath())
	if err != nil {
		logger.WithError(err).Error("Unpublish failed")
		return nil, status.Errorf(codes.Internal, "unpublish failed: %v", err)
	}

	// Unstage: decrement refcount, unmount blobfuse2 if refcount=0.
	if key != "" {
		if err := s.manager.Unstage(key); err != nil {
			logger.WithError(err).Error("Unstage failed")
			return nil, status.Errorf(codes.Internal, "unstage failed: %v", err)
		}
	}

	logger.Info("NodeUnpublishVolume succeeded")
	return &csi.NodeUnpublishVolumeResponse{}, nil
}

func configFromAttributes(
	attrs map[string]string,
	readOnly bool,
) (*blobfuse.MountConfig, error) {
	spec, err := contracts.ParseNodeVolumeAttributes(attrs, readOnly)
	if err != nil {
		return nil, err
	}

	config := blobfuse.MountConfig(spec)
	return &config, nil
}
