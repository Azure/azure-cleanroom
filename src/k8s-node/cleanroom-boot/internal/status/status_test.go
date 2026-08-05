// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package status

import (
	"testing"
)

func TestBootStatusSetComponent(t *testing.T) {
	bs := New()

	bs.SetComponent("netplan", "succeeded")
	if bs.Components["netplan"]["status"] != "succeeded" {
		t.Errorf(
			"expected succeeded, got %s",
			bs.Components["netplan"]["status"],
		)
	}

	bs.SetComponent("gpu", "failed", WithError("no device"))
	comp := bs.Components["gpu"]
	if comp["status"] != "failed" {
		t.Errorf("expected failed, got %s", comp["status"])
	}
	if comp["error"] != "no device" {
		t.Errorf("expected 'no device', got %s", comp["error"])
	}
}

func TestBootStatusInitialState(t *testing.T) {
	bs := New()
	if bs.Status != "initializing" {
		t.Errorf("expected initializing, got %s", bs.Status)
	}
	// On test machines, /etc/cleanroom-image-version won't exist.
	if bs.ImageVersion != "unknown" {
		t.Errorf("expected unknown, got %s", bs.ImageVersion)
	}
}
