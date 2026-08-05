// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// mount.go is the canonical home for blobfuse2 mount orchestration and sidecar
// secret resolution. Both deployment modes — CSI DaemonSet and proxy sidecar —
// pass through doMount here, so this is the single authoritative path for:
//   - Identity registration with the identity sidecar (:8290)
//   - DEK unwrap from the secrets sidecar (:9300)
//   - blobfuse2 process lifecycle (single-stage and CSE two-stage)
//
// The CSI driver (pkg/blobfuse/lifecycle.go) does NOT call sidecars directly
// for mounts; it passes semantic config fields to the proxy which resolves
// them via resolveSecrets below.
package main

import (
	"bufio"
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/contracts"
	"github.com/azure/azure-cleanroom/poc/csi-driver/pkg/sidecar"
	log "github.com/sirupsen/logrus"
	"gopkg.in/yaml.v3"
)

func doMount(req *MountRequest) error {
	if err := validateMountRequest(req); err != nil {
		return fmt.Errorf("invalid mount request: %w", err)
	}

	if err := resolveSecrets(req); err != nil {
		return fmt.Errorf("resolve secrets: %w", err)
	}

	if req.EncryptionMode == "CSE" {
		return doCSETwoStage(req)
	}

	return execBlobfuse(req)
}

func resolveSecrets(req *MountRequest) error {
	// resolveSecrets is the single place where identity registration and DEK
	// unwrap happen. Both modes reach this: DaemonSet mode via the Unix socket,
	// sidecar mode directly from runMountAll. Idempotent — skips any step whose
	// result is already in the Env map.
	if req.Env == nil {
		req.Env = make(map[string]string)
	}
	if req.IdentityClientID != "" && req.Env["MSI_ENDPOINT"] == "" {
		if err := registerIdentity(req.IdentityClientID, req.IdentitySubject, req.ContractID); err != nil {
			return fmt.Errorf("identity registration: %w", err)
		}
		req.Env["AZURE_STORAGE_AUTH_TYPE"] = "msi"
		req.Env["MSI_ENDPOINT"] = fmt.Sprintf(
			"%s/metadata/identity/%s/%s/oauth2/token",
			identityEndpoint, req.IdentityTenantID, req.IdentityClientID,
		)
	}

	if req.WrappedDekAkvEndpoint != "" && req.Env["ENCRYPTION_KEY"] == "" {
		dek, err := unwrapDEK(req)
		if err != nil {
			return fmt.Errorf("DEK unwrap: %w", err)
		}
		req.Env["ENCRYPTION_KEY"] = dek
	}

	return nil
}

// registerIdentity is the canonical implementation for registering an MSI
// federated credential with the identity sidecar. Used by both DaemonSet and
// sidecar modes via resolveSecrets — there is no duplicate copy elsewhere.
func registerIdentity(clientID, subject, contractID string) error {
	prefix := contracts.GovernanceAPIPathPrefix(contractID, subject)
	identityClient := sidecar.NewIdentityClient(identityEndpoint, sidecar.DefaultRetryPolicy())
	req := contracts.RegisterIdentityRequest{
		ClientID: clientID,
		Credential: contracts.RegisterIdentityCredentialSpec{
			CredentialType: "FederatedCredential",
			FederationConfiguration: contracts.FederatedCredentialConfiguration{
				IDTokenEndpoint:         idTokenEndpoint,
				Subject:                 subject,
				Audience:                contracts.OIDCAudience,
				GovernanceAPIPathPrefix: prefix,
			},
		},
	}

	if err := identityClient.Register(context.Background(), req); err != nil {
		return fmt.Errorf("POST identity/register: %w", err)
	}

	log.WithField("clientId", clientID).Info("Identity registered")
	return nil
}

// unwrapDEK is the canonical implementation for fetching a decrypted DEK from
// the secrets sidecar. Used by both DaemonSet and sidecar modes via
// resolveSecrets — there is no duplicate copy elsewhere.
func unwrapDEK(req *MountRequest) (string, error) {
	secretsClient := sidecar.NewSecretsClient(secretsEndpoint, sidecar.DefaultRetryPolicy())
	sidecarReq := contracts.UnwrapDEKRequest{
		ClientID:    req.IdentityClientID,
		TenantID:    req.IdentityTenantID,
		KID:         req.WrappedDekSecret,
		AKVEndpoint: req.WrappedDekAkvEndpoint,
		KEK: contracts.KEKSpec{
			KID:         req.KID,
			AKVEndpoint: req.AkvEndpoint,
			MAAEndpoint: req.MaaEndpoint,
		},
	}

	value, err := secretsClient.UnwrapDEK(context.Background(), sidecarReq)
	if err != nil {
		return "", fmt.Errorf("POST secrets/unwrap: %w", err)
	}
	return value, nil
}

func validateMountRequest(req *MountRequest) error {
	spec := MountSpec{
		StorageAccount:      req.Env["AZURE_STORAGE_ACCOUNT"],
		StorageContainer:    req.Env["AZURE_STORAGE_ACCOUNT_CONTAINER"],
		StorageBlobEndpoint: req.Env["AZURE_STORAGE_BLOB_ENDPOINT"],
		ClientID:            req.IdentityClientID,
		TenantID:            req.IdentityTenantID,
		Subject:             req.IdentitySubject,
		ContractID:          req.ContractID,
		EncryptionMode:      req.EncryptionMode,
		WrappedDekSecret:    req.WrappedDekSecret,
		WrappedDekAkvEP:     req.WrappedDekAkvEndpoint,
		KID:                 req.KID,
		AkvEndpoint:         req.AkvEndpoint,
		MaaEndpoint:         req.MaaEndpoint,
		ReadOnly:            req.ReadOnly,
		UseAdls:             req.UseAdls,
		SubDirectory:        req.SubDirectory,
		BlockSizeMB:         req.BlockSizeMB,
	}
	spec.Normalize()
	if err := spec.Validate(); err != nil {
		return err
	}
	req.BlockSizeMB = spec.BlockSizeMB
	req.EncryptionMode = spec.EncryptionMode
	return nil
}

func doCSETwoStage(req *MountRequest) error {
	plainPath := req.MountPath + "-plain"

	stage1Env := make(map[string]string, len(req.Env))
	for k, v := range req.Env {
		if k != "ENCRYPTION_KEY" {
			stage1Env[k] = v
		}
	}
	stage1 := *req
	stage1.MountPath = plainPath
	stage1.EncryptionMode = "SSE"
	stage1.Encrypted = false
	stage1.Env = stage1Env

	if err := execBlobfuse(&stage1); err != nil {
		return fmt.Errorf("CSE stage 1 (SSE plain): %w", err)
	}

	req.Encrypted = true
	if err := execBlobfuse(req); err != nil {
		if uerr := doUnmountPath(plainPath); uerr != nil {
			log.WithError(uerr).WithField("path", plainPath).Warn("Stage 1 cleanup failed")
		}
		return fmt.Errorf("CSE stage 2 (encrypted overlay): %w", err)
	}

	return nil
}

func execBlobfuse(req *MountRequest) error {
	stagingDir := stagingDirFor(req)
	if err := os.MkdirAll(stagingDir, 0755); err != nil {
		return fmt.Errorf("mkdir staging %s: %w", stagingDir, err)
	}
	if err := os.MkdirAll(req.MountPath, 0755); err != nil {
		return fmt.Errorf("mkdir %s: %w", req.MountPath, err)
	}

	cacheDir := cacheDirFor(req)
	_ = os.RemoveAll(cacheDir)
	if err := os.MkdirAll(cacheDir, 0755); err != nil {
		return fmt.Errorf("mkdir cache %s: %w", cacheDir, err)
	}

	env := make([]string, 0, len(os.Environ()))
	for _, e := range os.Environ() {
		if req.EncryptionMode != "CSE" && strings.HasPrefix(e, "BLOBFUSE_PLUGIN_PATH=") {
			continue
		}
		env = append(env, e)
	}
	for k, v := range req.Env {
		env = append(env, fmt.Sprintf("%s=%s", k, v))
	}

	var args []string
	if req.EncryptionMode == "CSE" {
		a, err := encryptedMountArgs(req, stagingDir, cacheDir)
		if err != nil {
			return err
		}
		args = a
	} else {
		args = unencryptedMountArgs(req, stagingDir, cacheDir)
	}

	log.WithFields(log.Fields{
		"mountPath":   req.MountPath,
		"encryptionMode": req.EncryptionMode,
		"args":        strings.Join(args, " "),
		"account":     req.Env["AZURE_STORAGE_ACCOUNT"],
		"container":   req.Env["AZURE_STORAGE_ACCOUNT_CONTAINER"],
		"msiEndpoint": req.Env["MSI_ENDPOINT"],
		"authType":    req.Env["AZURE_STORAGE_AUTH_TYPE"],
		"logFile":     mountLogFile(req, stagingDir),
	}).Info("Starting blobfuse2 mount")

	cmd := exec.Command(blobfuse2BinaryPath(), args...)
	cmd.Dir = stagingDir
	cmd.Env = env

	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return fmt.Errorf("stdout pipe: %w", err)
	}
	cmd.Stderr = cmd.Stdout

	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start blobfuse2: %w", err)
	}

	bfLog := log.WithField("component", "blobfuse2")
	scanner := bufio.NewScanner(stdout)
	for scanner.Scan() {
		bfLog.Info(scanner.Text())
	}

	if err := cmd.Wait(); err != nil {
		if isMountpoint(req.MountPath) {
			log.WithError(err).WithField("mountPath", req.MountPath).
				Warn("blobfuse2 exited non-zero but mountpoint is healthy")
			emitBlobfuseLog(req, stagingDir)
			return nil
		}
		emitBlobfuseLog(req, stagingDir)
		return fmt.Errorf("blobfuse2 mount failed: %w", err)
	}
	emitBlobfuseLog(req, stagingDir)
	return nil
}

func emitBlobfuseLog(req *MountRequest, stagingDir string) {
	logFile := mountLogFile(req, stagingDir)
	data, err := os.ReadFile(logFile)
	if err != nil {
		log.WithError(err).WithField("logFile", logFile).Warn("Could not read blobfuse2 log file")
		return
	}
	bfLog := log.WithFields(log.Fields{
		"component": "blobfuse2-logfile",
		"logFile":   logFile,
	})
	for _, line := range strings.Split(string(data), "\n") {
		if line != "" {
			bfLog.Info(line)
		}
	}
}

func encryptedMountArgs(req *MountRequest, stagingDir string, cacheDir string) ([]string, error) {
	cfgPath := filepath.Join(stagingDir, "config.yaml")

	var cfg map[string]interface{}
	if err := yaml.Unmarshal(encryptorConfigTemplate, &cfg); err != nil {
		return nil, fmt.Errorf("parse encryptor-config.yaml template: %w", err)
	}
	if cfg == nil {
		cfg = make(map[string]interface{})
	}

	bc := ensureSection(cfg, "block_cache")
	bc["block-size-mb"] = 16
	bc["path"] = cacheDir

	enc := ensureSection(cfg, "encryptor")
	enc["block-size-mb"] = 16
	enc["encrypted-mount-path"] = req.MountPath + "-plain/"

	out, err := yaml.Marshal(cfg)
	if err != nil {
		return nil, fmt.Errorf("marshal config.yaml: %w", err)
	}
	if err := os.WriteFile(cfgPath, out, 0640); err != nil {
		return nil, fmt.Errorf("write config.yaml: %w", err)
	}

	logFile := mountLogFile(req, stagingDir)
	args := []string{
		"mount", req.MountPath,
		fmt.Sprintf("--config-file=%s", cfgPath),
		fmt.Sprintf("--read-only=%v", req.ReadOnly),
		"--log-file-path", logFile,
	}
	if req.SubDirectory != "" {
		args = append(args, "--subdirectory", req.SubDirectory)
	}
	return args, nil
}

func unencryptedMountArgs(req *MountRequest, stagingDir string, cacheDir string) []string {
	blockSizeMB := req.BlockSizeMB
	if blockSizeMB <= 0 {
		blockSizeMB = 16
	}

	logFile := mountLogFile(req, stagingDir)
	args := []string{
		"mount", req.MountPath,
		"--allow-other",
		fmt.Sprintf("--read-only=%v", req.ReadOnly),
		fmt.Sprintf("--cpk-enabled=%v", req.CpkEnabled),
		"--virtual-directory=true",
		"--block-cache",
		"--block-cache-path", cacheDir,
		fmt.Sprintf("--block-cache-block-size=%d", blockSizeMB),
		"--block-cache-pool-size=256",
		"--block-cache-prefetch=0",
		"--block-cache-disk-size=4096",
		"--log-file-path", logFile,
		"--log-level=LOG_INFO",
		fmt.Sprintf("--use-adls=%v", req.UseAdls),
		fmt.Sprintf("--disable-writeback-cache=%v", req.DisableWriteback),
	}
	if req.SubDirectory != "" {
		args = append(args, "--subdirectory", req.SubDirectory)
	}
	return args
}

// doUnmount unmounts the blobfuse2 mount at path and, if this was a CSE
// two-stage mount, also unmounts the underlying plain SSE layer at path+"-plain".
// The plain-path cleanup is best-effort: a warning is logged on failure but the
// error is not propagated so that the primary unmount result is preserved.
func doUnmount(path string) error {
	cmd := exec.Command(blobfuse2BinaryPath(), "unmount", path)
	if out, err := cmd.CombinedOutput(); err != nil {
		cmd2 := exec.Command("fusermount3", "-u", path)
		if out2, err2 := cmd2.CombinedOutput(); err2 != nil {
			return fmt.Errorf("blobfuse2 unmount: %s; fusermount3: %s",
				strings.TrimSpace(string(out)), strings.TrimSpace(string(out2)))
		}
	}

	// CSE mounts have a plain SSE underlayer at path+"-plain". Clean it up too.
	plainPath := path + "-plain"
	if isMountpoint(plainPath) {
		if err := doUnmountPath(plainPath); err != nil {
			log.WithError(err).WithField("path", plainPath).Warn(
				"CSE plain-layer cleanup failed; mount may have already been unmounted")
		}
	}

	return nil
}

// doUnmountPath is the low-level single-path unmount used by doUnmount and
// the CSE stage-1 error cleanup. It does NOT attempt plain-layer cleanup.
func doUnmountPath(path string) error {
	cmd := exec.Command(blobfuse2BinaryPath(), "unmount", path)
	if out, err := cmd.CombinedOutput(); err != nil {
		cmd2 := exec.Command("fusermount3", "-u", path)
		if out2, err2 := cmd2.CombinedOutput(); err2 != nil {
			return fmt.Errorf("blobfuse2 unmount: %s; fusermount3: %s",
				strings.TrimSpace(string(out)), strings.TrimSpace(string(out2)))
		}
	}
	return nil
}

// ensureSection returns the nested map at cfg[key], creating it if absent.
func ensureSection(cfg map[string]interface{}, key string) map[string]interface{} {
	if v, ok := cfg[key]; ok {
		if m, ok := v.(map[string]interface{}); ok {
			return m
		}
	}
	m := make(map[string]interface{})
	cfg[key] = m
	return m
}

// cacheDirFor returns the block cache directory for a mount request.
// Uses BLOBFUSE_CACHE_PATH from Env when set, otherwise derives from mount path.
func cacheDirFor(req *MountRequest) string {
	if cachePath, ok := req.Env["BLOBFUSE_CACHE_PATH"]; ok && cachePath != "" {
		return cachePath
	}
	return "/tmp/blobfuse_cache_" + filepath.Base(req.MountPath)
}

// stagingDirFor returns the staging directory for a mount request.
// Uses StagingDir when set, otherwise derives from the structure of MountPath.
func stagingDirFor(req *MountRequest) string {
	if req.StagingDir != "" {
		return req.StagingDir
	}
	if filepath.Base(req.MountPath) == "mount" {
		return filepath.Dir(req.MountPath)
	}
	return "/tmp/blobfuse_staging_" + filepath.Base(req.MountPath)
}

// mountLogFile returns the path of the blobfuse2 log file for a given mount.
func mountLogFile(req *MountRequest, stagingDir string) string {
	_ = os.MkdirAll(stagingDir, 0755)
	return filepath.Join(stagingDir, filepath.Base(req.MountPath)+"-blobfuse.log")
}

// isMountpoint reports whether path is currently an active mount point.
// The proxy binary cannot import from pkg/blobfuse (circular dependency),
// so this thin wrapper lives here. The canonical copy is in
// pkg/blobfuse/recovery_helpers.go with an identical implementation.
func isMountpoint(path string) bool {
	return exec.Command("mountpoint", "-q", path).Run() == nil
}

