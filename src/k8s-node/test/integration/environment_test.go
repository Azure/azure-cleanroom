// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

//go:build integration

package integration

import (
	"path/filepath"
	"testing"
)

// Environment is the seam between the integration tests and the concrete
// infrastructure they run against. Exactly one adapter compiles per run,
// selected by build tag (aks vs kind); currentEnv() is defined in the
// matching helpers file. Shared test bodies call through `env` so no
// environment-specific behaviour leaks into them.
type Environment interface {
	Name() string
	Init() error
	Teardown()

	ExecOnNode(args ...string) (string, error)
	CopyFileToNode(t *testing.T, localPath, remotePath string) error
	CurlKubeletAPI(path string) (httpCode int, body string, err error)
	FlexNodeName() string

	DeployAPIServerProxy() error
	DeployKubeletProxy() error
	UninstallProxy(name, script string) (string, error)

	SigningKeyDir() string
	KubeletProxyInstallMode() string

	SetKubeletMaxPods(t *testing.T, maxPods int) int
	RestoreKubeletMaxPods(t *testing.T, original int)
}

// env is the build-tag-selected environment adapter for this run.
var env = currentEnv()

// signingCertPath / signingKeyPath are derived from the environment's signing
// key directory, so they stay free helpers over the interface.
func signingCertPath() string {
	return filepath.Join(env.SigningKeyDir(), "signing-cert.pem")
}

func signingKeyPath() string {
	return filepath.Join(env.SigningKeyDir(), "signing-key.pem")
}
