// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Package status tracks and serializes boot pipeline status.
package status

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

const (
	statusDir        = "/var/run/cleanroom"
	StatusFile       = statusDir + "/status.json"
	imageVersionFile = "/etc/cleanroom-image-version"
)

// BootStatus tracks the current state of the boot pipeline.
type BootStatus struct {
	Status        string                       `json:"status"`
	Stage         string                       `json:"stage"`
	Error         string                       `json:"error"`
	ConfigVersion string                       `json:"configVersion"`
	ImageVersion  string                       `json:"imageVersion"`
	Components    map[string]map[string]string `json:"components"`
}

// New creates a new BootStatus in the "initializing" state.
func New() *BootStatus {
	return &BootStatus{
		Status:       "initializing",
		ImageVersion: readImageVersion(),
		Components:   make(map[string]map[string]string),
	}
}

// SetComponent records the outcome of a stage.
func (b *BootStatus) SetComponent(
	name, status string,
	opts ...ComponentOption,
) {
	entry := map[string]string{"status": status}
	for _, opt := range opts {
		opt(entry)
	}
	b.Components[name] = entry
}

// ComponentOption is a functional option for SetComponent.
type ComponentOption func(map[string]string)

// WithError adds an error message to the component entry.
func WithError(err string) ComponentOption {
	return func(m map[string]string) { m["error"] = err }
}

// WithReason adds a reason to the component entry.
func WithReason(reason string) ComponentOption {
	return func(m map[string]string) { m["reason"] = reason }
}

// Write serializes the current status to /var/run/cleanroom/status.json.
func (b *BootStatus) Write() error {
	if err := os.MkdirAll(statusDir, 0o755); err != nil {
		return fmt.Errorf("creating status dir: %w", err)
	}

	data := map[string]any{
		"status":        b.Status,
		"stage":         b.Stage,
		"error":         b.Error,
		"timestamp":     time.Now().UTC().Format("2006-01-02T15:04:05Z"),
		"configVersion": b.ConfigVersion,
		"imageVersion":  b.ImageVersion,
		"components":    b.Components,
	}

	jsonBytes, err := json.MarshalIndent(data, "", "  ")
	if err != nil {
		return fmt.Errorf("marshalling status: %w", err)
	}
	jsonBytes = append(jsonBytes, '\n')

	return os.WriteFile(
		filepath.Join(statusDir, "status.json"),
		jsonBytes, 0o644,
	)
}

func readImageVersion() string {
	data, err := os.ReadFile(imageVersionFile)
	if err != nil {
		return "unknown"
	}
	return strings.TrimSpace(string(data))
}
