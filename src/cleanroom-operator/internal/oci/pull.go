// Package oci provides helpers for pulling OCI artifacts.
package oci

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"oras.land/oras-go/v2"
	"oras.land/oras-go/v2/content/file"
	"oras.land/oras-go/v2/registry/remote"
)

// PullFileContent pulls an OCI artifact from the given image
// reference and returns the content of the specified file.
// The image reference is expected to be a plain ORAS artifact
// (not a container image) containing the target file as a
// layer.
func PullFileContent(
	ctx context.Context,
	imageRef string,
	filename string,
) ([]byte, error) {
	// Create a temp directory for the pull.
	tmpDir, err := os.MkdirTemp("", "oci-pull-*")
	if err != nil {
		return nil, fmt.Errorf(
			"creating temp dir: %w", err,
		)
	}
	defer os.RemoveAll(tmpDir)

	// Use ORAS file store as the target.
	fileStore, err := file.New(tmpDir)
	if err != nil {
		return nil, fmt.Errorf(
			"creating file store: %w", err,
		)
	}
	defer fileStore.Close()

	// Parse the image reference to get repo and tag.
	repo, err := remote.NewRepository(imageRef)
	if err != nil {
		return nil, fmt.Errorf(
			"creating repository: %w", err,
		)
	}

	// PlainHTTP for local Kind registry only.
	host := repo.Reference.Host()
	repo.PlainHTTP = strings.HasPrefix(host, "localhost") ||
		strings.HasPrefix(host, "127.0.0.1") ||
		strings.HasPrefix(host, "ccr-registry")

	// Copy the artifact from the registry to the file
	// store.
	_, err = oras.Copy(
		ctx,
		repo,
		imageRef,
		fileStore,
		"",
		oras.CopyOptions{},
	)
	if err != nil {
		return nil, fmt.Errorf(
			"pulling OCI artifact %s: %w",
			imageRef, err,
		)
	}

	// Read the target file.
	filePath := filepath.Join(tmpDir, filename)
	data, err := os.ReadFile(filePath)
	if err != nil {
		return nil, fmt.Errorf(
			"reading %s from artifact: %w",
			filename, err,
		)
	}

	return data, nil
}
