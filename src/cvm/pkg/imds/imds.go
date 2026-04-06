// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// Package imds provides helpers for fetching VM metadata from the Azure
// Instance Metadata Service (IMDS).
//
// Endpoint:
//
//	GET http://169.254.169.254/metadata/instance/compute/storageProfile/imageReference?api-version=2025-04-07
//	Header: Metadata: true
//
// Response (JSON):
//
//	{
//	  "id":                      "",
//	  "offer":                   "0001-com-ubuntu-confidential-vm-jammy",
//	  "publisher":               "Canonical",
//	  "sku":                     "22_04-lts-cvm",
//	  "version":                 "22.04.202603010",
//	  "communityGalleryImageId": "",
//	  "sharedGalleryImageId":    "",
//	  "exactVersion":            "22.04.202603010"
//	}
package imds

import (
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"time"
)

const (
	imageReferenceURL = "http://169.254.169.254/metadata/instance/compute/" +
		"storageProfile/imageReference?api-version=2025-04-07"
)

// ImageReference contains the VM image details returned by IMDS.
type ImageReference struct {
	ID                      string `json:"id"`
	Offer                   string `json:"offer"`
	Publisher               string `json:"publisher"`
	Sku                     string `json:"sku"`
	Version                 string `json:"version"`
	CommunityGalleryImageID string `json:"communityGalleryImageId"`
	SharedGalleryImageID    string `json:"sharedGalleryImageId"`
	ExactVersion            string `json:"exactVersion"`
}

// FetchImageReference queries the Azure IMDS endpoint and returns the VM's
// image reference (publisher, offer, SKU, version).
func FetchImageReference() (*ImageReference, error) {
	client := &http.Client{Timeout: 30 * time.Second}

	req, err := http.NewRequest(http.MethodGet, imageReferenceURL, nil)
	if err != nil {
		return nil, fmt.Errorf("create IMDS request: %w", err)
	}
	req.Header.Set("Metadata", "true")

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("IMDS HTTP GET: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(resp.Body)
		return nil, fmt.Errorf("IMDS returned HTTP %d: %s", resp.StatusCode, string(body))
	}

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, fmt.Errorf("read IMDS response: %w", err)
	}

	var imgRef ImageReference
	if err := json.Unmarshal(body, &imgRef); err != nil {
		return nil, fmt.Errorf("parse IMDS JSON: %w", err)
	}

	return &imgRef, nil
}
