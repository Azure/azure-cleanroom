package azure

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

const (
	hfAPIBase = "https://huggingface.co/api/models"
	hfResolve = "https://huggingface.co/%s/resolve/main/%s"
)

// HFSibling represents a file entry in a HuggingFace
// model repository.
type HFSibling struct {
	RFilename string `json:"rfilename"`
}

// HFModelInfo is the subset of the HuggingFace API
// response we need for model file discovery.
type HFModelInfo struct {
	Siblings []HFSibling `json:"siblings"`
}

// HFModelType classifies a HuggingFace model repo.
type HFModelType int

const (
	// HFModelTypeGGUF indicates a GGUF quantized model.
	HFModelTypeGGUF HFModelType = iota
	// HFModelTypeSafetensors indicates a safetensors
	// sharded model.
	HFModelTypeSafetensors
)

// HFModelPlan describes which files to upload and where.
type HFModelPlan struct {
	// Type is the detected model type.
	Type HFModelType
	// Files is the list of files to copy from HF.
	Files []string
	// BlobPrefix is the blob storage prefix (e.g.
	// "models/tinyllama-chat-gguf").
	BlobPrefix string
	// BlobPath is the final model blob path for the
	// governance document (e.g.
	// "models/tinyllama-chat-gguf/model.gguf" or
	// "models/gemma4-31b-it").
	BlobPath string
}

// FetchHFModelInfo queries the HuggingFace API for
// model repo metadata.
func FetchHFModelInfo(
	ctx context.Context,
	modelID string,
) (*HFModelInfo, error) {
	url := hfAPIBase + "/" + modelID

	client := &http.Client{Timeout: 30 * time.Second}
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"creating HF API request: %w", err,
		)
	}

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf(
			"GET %s: %w", url, err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		return nil, fmt.Errorf(
			"HF API returned %d for %s: %s",
			resp.StatusCode, modelID, string(body),
		)
	}

	var info HFModelInfo
	if err := json.NewDecoder(resp.Body).Decode(
		&info,
	); err != nil {
		return nil, fmt.Errorf(
			"decoding HF API response: %w", err,
		)
	}

	return &info, nil
}

// PlanModelUpload analyzes a HuggingFace model repo and
// produces an upload plan. sourceFile is optional and
// selects a specific GGUF file. blobPrefixOverride
// overrides the default blob prefix.
func PlanModelUpload(
	info *HFModelInfo,
	modelID string,
	sourceFile string,
	blobPrefixOverride string,
) (*HFModelPlan, error) {
	// Classify: collect GGUF and safetensors files.
	var ggufFiles []string
	var safetensorFiles []string
	var configFiles []string

	for _, s := range info.Siblings {
		name := s.RFilename
		switch {
		case strings.HasSuffix(name, ".gguf"):
			ggufFiles = append(ggufFiles, name)
		case strings.HasSuffix(name, ".safetensors"):
			safetensorFiles = append(
				safetensorFiles, name,
			)
		case isModelConfigFile(name):
			configFiles = append(configFiles, name)
		}
	}

	// Derive default blob prefix from model ID.
	blobPrefix := defaultBlobPrefix(modelID)
	if blobPrefixOverride != "" {
		blobPrefix = blobPrefixOverride
	}

	if len(ggufFiles) > 0 {
		return planGGUF(
			ggufFiles, sourceFile, blobPrefix,
		)
	}

	if len(safetensorFiles) > 0 {
		return planSafetensors(
			configFiles, safetensorFiles, blobPrefix,
		)
	}

	return nil, fmt.Errorf(
		"model %s has no .gguf or .safetensors files",
		modelID,
	)
}

// planGGUF builds the upload plan for a GGUF repo.
func planGGUF(
	ggufFiles []string,
	sourceFile string,
	blobPrefix string,
) (*HFModelPlan, error) {
	var selected string

	if sourceFile != "" {
		// Validate that the requested file exists.
		found := false
		for _, f := range ggufFiles {
			if f == sourceFile {
				found = true
				break
			}
		}
		if !found {
			return nil, fmt.Errorf(
				"sourceFile %q not found in repo; "+
					"available GGUF files: %s",
				sourceFile,
				strings.Join(ggufFiles, ", "),
			)
		}
		selected = sourceFile
	} else if len(ggufFiles) == 1 {
		selected = ggufFiles[0]
	} else {
		return nil, fmt.Errorf(
			"repo contains %d GGUF files; set "+
				"spec.model.sourceFile to one of: %s",
			len(ggufFiles),
			strings.Join(ggufFiles, ", "),
		)
	}

	// GGUF models are single-file; upload as
	// <prefix>/model.gguf.
	return &HFModelPlan{
		Type:       HFModelTypeGGUF,
		Files:      []string{selected},
		BlobPrefix: blobPrefix,
		BlobPath:   blobPrefix + "/model.gguf",
	}, nil
}

// planSafetensors builds the upload plan for a
// safetensors repo.
func planSafetensors(
	configFiles []string,
	safetensorFiles []string,
	blobPrefix string,
) (*HFModelPlan, error) {
	files := make([]string, 0,
		len(configFiles)+len(safetensorFiles),
	)
	files = append(files, configFiles...)
	files = append(files, safetensorFiles...)

	return &HFModelPlan{
		Type:       HFModelTypeSafetensors,
		Files:      files,
		BlobPrefix: blobPrefix,
		BlobPath:   blobPrefix,
	}, nil
}

// defaultBlobPrefix derives the blob storage prefix
// from a HuggingFace model ID.
func defaultBlobPrefix(modelID string) string {
	repo := modelID
	if idx := strings.LastIndex(modelID, "/"); idx >= 0 {
		repo = modelID[idx+1:]
	}
	return strings.ToLower(repo)
}

// HFResolveURL returns the HuggingFace download URL
// for a file in a model repo.
func HFResolveURL(
	modelID string,
	filename string,
) string {
	return fmt.Sprintf(hfResolve, modelID, filename)
}

// isModelConfigFile returns true for files that should
// be copied alongside model weights (config, tokenizer,
// etc.).
func isModelConfigFile(name string) bool {
	// Skip hidden files and READMEs.
	if strings.HasPrefix(name, ".") ||
		strings.EqualFold(name, "README.md") {
		return false
	}

	configSuffixes := []string{
		".json",
		".txt",
		".jinja",
		".model",
	}
	for _, suffix := range configSuffixes {
		if strings.HasSuffix(name, suffix) {
			return true
		}
	}
	return false
}
