package azure

import (
	"context"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"

	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob/blob"
	"github.com/Azure/azure-sdk-for-go/sdk/storage/azblob/blockblob"
	ctrllog "sigs.k8s.io/controller-runtime/pkg/log"
)

const (
	copyPollInterval = 5 * time.Second
	copyTimeout      = 30 * time.Minute
)

// UploadProgressFunc is called to report upload progress.
// index is 0-based, total is len(plan.Files).
type UploadProgressFunc func(
	file string, index int, total int, msg string,
)

// UploadModelFromHF uploads model files from HuggingFace
// to Azure Blob Storage using server-side copy. It
// resolves HTTP redirects (since Azure copy cannot follow
// them), aborts stale pending copies, and polls until
// each copy completes. The optional onProgress callback
// is invoked per-file to report progress.
func (c *Client) UploadModelFromHF(
	ctx context.Context,
	storageAccountName string,
	containerName string,
	modelID string,
	plan *HFModelPlan,
	onProgress UploadProgressFunc,
) error {
	log := ctrllog.FromContext(ctx)

	serviceURL := fmt.Sprintf(
		"https://%s.blob.core.windows.net",
		storageAccountName,
	)

	blobServiceClient, err := azblob.NewClient(
		serviceURL, c.credential, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating blob client: %w", err,
		)
	}

	// Check if all blobs already exist (idempotency).
	allExist := true
	for _, file := range plan.Files {
		blobName := blobNameForFile(plan, file)
		exists, eErr := blobExists(
			ctx, blobServiceClient,
			containerName, blobName,
		)
		if eErr != nil {
			return fmt.Errorf(
				"checking blob %s: %w", blobName, eErr,
			)
		}
		if !exists {
			allExist = false
			break
		}
	}
	if allExist {
		log.Info("All model blobs already exist, "+
			"skipping upload",
			"blobPrefix", plan.BlobPrefix,
			"fileCount", len(plan.Files))
		return nil
	}

	log.Info("Starting model upload from HuggingFace",
		"modelId", modelID,
		"blobPrefix", plan.BlobPrefix,
		"fileCount", len(plan.Files))

	for i, file := range plan.Files {
		blobName := blobNameForFile(plan, file)

		// Check if this specific blob exists already.
		exists, eErr := blobExists(
			ctx, blobServiceClient,
			containerName, blobName,
		)
		if eErr != nil {
			return fmt.Errorf(
				"checking blob %s: %w", blobName, eErr,
			)
		}
		if exists {
			log.Info("Blob already exists, skipping",
				"blob", blobName)
			if onProgress != nil {
				onProgress(
					file, i, len(plan.Files),
					fmt.Sprintf(
						"File %d/%d already "+
							"exists, skipping: %s",
						i+1, len(plan.Files),
						file,
					),
				)
			}
			continue
		}

		sourceURL := HFResolveURL(modelID, file)

		log.Info("Copying file to blob storage",
			"source", file,
			"blob", blobName)
		if onProgress != nil {
			onProgress(
				file, i, len(plan.Files),
				fmt.Sprintf(
					"Copying file %d/%d: %s",
					i+1, len(plan.Files), file,
				),
			)
		}

		if err := c.copyURLToBlob(
			ctx, blobServiceClient,
			containerName, blobName, sourceURL,
			func(progress string) {
				if onProgress != nil {
					onProgress(
						file, i, len(plan.Files),
						fmt.Sprintf(
							"Copying file %d/%d: %s (%s)",
							i+1, len(plan.Files),
							file, progress,
						),
					)
				}
			},
		); err != nil {
			return fmt.Errorf(
				"copying %s to %s: %w",
				file, blobName, err,
			)
		}

		log.Info("File copy completed",
			"blob", blobName)
		if onProgress != nil {
			onProgress(
				file, i, len(plan.Files),
				fmt.Sprintf(
					"Completed file %d/%d: %s",
					i+1, len(plan.Files), file,
				),
			)
		}
	}

	log.Info("Model upload completed",
		"blobPrefix", plan.BlobPrefix)
	return nil
}

// blobNameForFile computes the destination blob name
// for a given source file. For GGUF models the file is
// stored as model.gguf regardless of the source name.
func blobNameForFile(
	plan *HFModelPlan,
	file string,
) string {
	if plan.Type == HFModelTypeGGUF {
		return plan.BlobPrefix + "/model.gguf"
	}
	return plan.BlobPrefix + "/" + file
}

// blobExists checks if a blob exists and has non-zero
// content length.
func blobExists(
	ctx context.Context,
	client *azblob.Client,
	containerName string,
	blobName string,
) (bool, error) {
	blobClient := client.ServiceClient().
		NewContainerClient(containerName).
		NewBlobClient(blobName)

	props, err := blobClient.GetProperties(ctx, nil)
	if err != nil {
		if isBlobNotFound(err) {
			return false, nil
		}
		return false, err
	}

	if props.ContentLength != nil &&
		*props.ContentLength > 0 {
		return true, nil
	}
	return false, nil
}

// isBlobNotFound checks if an error indicates a 404.
func isBlobNotFound(err error) bool {
	return strings.Contains(err.Error(), "BlobNotFound") ||
		strings.Contains(err.Error(), "404")
}

// copyProgressFunc is called with a human-readable
// progress string during blob copy polling.
type copyProgressFunc func(progress string)

// copyURLToBlob resolves redirects on the source URL
// and starts a server-side blob copy, then polls until
// completion.
func (c *Client) copyURLToBlob(
	ctx context.Context,
	client *azblob.Client,
	containerName string,
	blobName string,
	sourceURL string,
	onCopyProgress copyProgressFunc,
) error {
	// Resolve redirects — Azure copy service cannot
	// follow HTTP redirects from HuggingFace CDN.
	resolvedURL, err := resolveRedirects(
		ctx, sourceURL,
	)
	if err != nil {
		return fmt.Errorf(
			"resolving redirects for %s: %w",
			sourceURL, err,
		)
	}

	blobClient := client.ServiceClient().
		NewContainerClient(containerName).
		NewBlockBlobClient(blobName)

	// Abort any pending copy from a previous run.
	if err := abortPendingCopy(
		ctx, blobClient,
	); err != nil {
		return fmt.Errorf(
			"aborting pending copy: %w", err,
		)
	}

	// Start server-side copy.
	copyResp, err := blobClient.StartCopyFromURL(
		ctx, resolvedURL, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"starting copy from %s: %w",
			resolvedURL, err,
		)
	}

	// If copy completed synchronously, we're done.
	if copyResp.CopyStatus != nil &&
		*copyResp.CopyStatus == blob.CopyStatusTypeSuccess {
		return nil
	}

	// Poll until copy completes.
	return pollCopyStatus(
		ctx, blobClient, blobName, onCopyProgress,
	)
}

// resolveRedirects follows HTTP redirects and returns
// the final URL.
func resolveRedirects(
	ctx context.Context,
	sourceURL string,
) (string, error) {
	httpClient := &http.Client{
		Timeout: 30 * time.Second,
		CheckRedirect: func(
			req *http.Request,
			via []*http.Request,
		) error {
			if len(via) >= 10 {
				return fmt.Errorf(
					"too many redirects",
				)
			}
			return nil
		},
	}

	req, err := http.NewRequestWithContext(
		ctx, http.MethodHead, sourceURL, nil,
	)
	if err != nil {
		return "", err
	}

	resp, err := httpClient.Do(req)
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	return resp.Request.URL.String(), nil
}

// abortPendingCopy checks for and aborts any pending
// copy operation on the blob.
func abortPendingCopy(
	ctx context.Context,
	blobClient *blockblob.Client,
) error {
	props, err := blobClient.GetProperties(ctx, nil)
	if err != nil {
		if isBlobNotFound(err) {
			return nil
		}
		return err
	}

	if props.CopyID != nil && props.CopyStatus != nil &&
		*props.CopyStatus ==
			blob.CopyStatusTypePending {
		log := ctrllog.FromContext(ctx)
		log.Info("Aborting pending copy",
			"copyId", *props.CopyID)
		_, err := blobClient.AbortCopyFromURL(
			ctx, *props.CopyID, nil,
		)
		if err != nil {
			// Ignore errors — copy may have completed
			// between check and abort.
			log.Info(
				"Abort copy returned error (ignoring)",
				"error", err.Error())
		}
	}
	return nil
}

// pollCopyStatus polls the blob copy status until it
// succeeds, fails, or times out.
func pollCopyStatus(
	ctx context.Context,
	blobClient *blockblob.Client,
	blobName string,
	onCopyProgress copyProgressFunc,
) error {
	log := ctrllog.FromContext(ctx)
	deadline := time.Now().Add(copyTimeout)
	ticker := time.NewTicker(copyPollInterval)
	defer ticker.Stop()

	lastPct := -1

	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-ticker.C:
			if time.Now().After(deadline) {
				return fmt.Errorf(
					"copy timed out for %s after %v",
					blobName, copyTimeout,
				)
			}

			props, err := blobClient.GetProperties(
				ctx, nil,
			)
			if err != nil {
				return fmt.Errorf(
					"getting copy status for %s: %w",
					blobName, err,
				)
			}

			if props.CopyStatus == nil {
				continue
			}

			switch *props.CopyStatus {
			case blob.CopyStatusTypeSuccess:
				return nil
			case blob.CopyStatusTypeFailed:
				desc := ""
				if props.CopyStatusDescription != nil {
					desc = *props.CopyStatusDescription
				}
				return fmt.Errorf(
					"copy failed for %s: %s",
					blobName, desc,
				)
			case blob.CopyStatusTypeAborted:
				return fmt.Errorf(
					"copy aborted for %s", blobName,
				)
			case blob.CopyStatusTypePending:
				if props.CopyProgress != nil {
					log.Info("Copy in progress",
						"blob", blobName,
						"progress",
						*props.CopyProgress)
					copied, total :=
						parseCopyProgress(
							*props.CopyProgress,
						)
					if total > 0 &&
						onCopyProgress != nil {
						pct := int(
							float64(copied) /
								float64(total) *
								100,
						)
						if pct-lastPct >= 5 {
							lastPct = pct
							onCopyProgress(
								fmt.Sprintf(
									"%s / %s, %d%%",
									humanBytes(
										copied,
									),
									humanBytes(
										total,
									),
									pct,
								),
							)
						}
					}
				}
			}
		}
	}
}

// parseCopyProgress parses Azure's CopyProgress string
// format "bytesCopied/totalBytes" into two int64 values.
func parseCopyProgress(
	progress string,
) (copied int64, total int64) {
	parts := strings.SplitN(progress, "/", 2)
	if len(parts) != 2 {
		return 0, 0
	}
	copied, _ = strconv.ParseInt(
		strings.TrimSpace(parts[0]), 10, 64,
	)
	total, _ = strconv.ParseInt(
		strings.TrimSpace(parts[1]), 10, 64,
	)
	return copied, total
}

// humanBytes formats a byte count into a human-readable
// string (e.g. "1.5 GB", "256 MB").
func humanBytes(b int64) string {
	const (
		gb = 1 << 30
		mb = 1 << 20
		kb = 1 << 10
	)
	switch {
	case b >= gb:
		return fmt.Sprintf(
			"%.1f GB", float64(b)/float64(gb),
		)
	case b >= mb:
		return fmt.Sprintf(
			"%.0f MB", float64(b)/float64(mb),
		)
	case b >= kb:
		return fmt.Sprintf(
			"%.0f KB", float64(b)/float64(kb),
		)
	default:
		return fmt.Sprintf("%d B", b)
	}
}
