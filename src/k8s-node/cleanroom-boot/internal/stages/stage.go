// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Package stage defines the boot pipeline stage interface and context.
package stages

import (
	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/config"
	"github.com/azure/azure-cleanroom/src/k8s-node/cleanroom-boot/internal/status"
)

// Stage is the interface that every boot stage must implement.
type Stage interface {
	// Name returns a short identifier used in status.json components.
	Name() string
	// Run executes the stage. Returns an error on failure.
	Run(ctx *Context) error
}

// Context holds the static inputs passed to every stage. Stages do not mutate
// it: cross-stage hand-offs go through the filesystem (the boot contract).
type Context struct {
	ConfigPath        string
	BootDir           string
	ApiServerProxyDir string
	KubeletProxyDir   string
	BootStatus        *status.BootStatus

	// Populated by the validate stage; available to subsequent stages.
	Config *config.CleanroomConfig
}
