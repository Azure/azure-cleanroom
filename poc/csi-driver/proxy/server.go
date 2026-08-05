// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// server.go handles the Unix socket connection lifecycle and dispatches
// incoming JSON requests to the mount/unmount handlers in mount.go.
package main

import (
	"encoding/json"
	"fmt"
	"io"
	"net"

	log "github.com/sirupsen/logrus"
)

func handleConn(conn net.Conn) {
	defer func() { _ = conn.Close() }()

	var req MountRequest
	dec := json.NewDecoder(conn)
	if err := dec.Decode(&req); err != nil {
		if err != io.EOF {
			log.WithError(err).Error("Failed to decode request")
		}
		return
	}

	var resp Response
	switch req.Op {
	case "mount":
		if err := doMount(&req); err != nil {
			log.WithError(err).WithField("mountPath", req.MountPath).Error("Mount failed")
			resp = Response{Success: false, Error: err.Error()}
		} else {
			resp = Response{Success: true}
		}
	case "unmount":
		if err := doUnmount(req.MountPath); err != nil {
			log.WithError(err).WithField("mountPath", req.MountPath).Error("Unmount failed")
			resp = Response{Success: false, Error: err.Error()}
		} else {
			resp = Response{Success: true}
		}
	default:
		resp = Response{Success: false, Error: fmt.Sprintf("unknown op: %s", req.Op)}
	}

	if err := json.NewEncoder(conn).Encode(resp); err != nil {
		log.WithError(err).Error("Failed to encode response")
	}
}
