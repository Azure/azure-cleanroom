// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// types.go contains type aliases that expose the shared wire protocol types
// from pkg/contracts as package-level names for use within the proxy binary.
// No duplicate definitions live here — all wire types are canonical in contracts.
package main

import "github.com/azure/azure-cleanroom/poc/csi-driver/pkg/contracts"

// MountRequest is the canonical mount request type shared by:
//   - The CSI driver, which sends it over the Unix socket.
//   - The proxy sidecar, which reads it from BLOBFUSE_MOUNTS_JSON.
//
// See contracts.ProxyMountRequest for full field documentation.
type MountRequest = contracts.ProxyMountRequest

// Response is the canonical response returned by the proxy after a mount or
// unmount operation. See contracts.ProxyResponse for field documentation.
type Response = contracts.ProxyResponse

// MountSpec aliases the shared canonical volume attribute contract so proxy
// code can use the short name consistently with the rest of the codebase.
type MountSpec = contracts.MountSpec
