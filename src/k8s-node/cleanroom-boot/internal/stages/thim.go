// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

package stages

import (
	"fmt"
	"io"
	"net/http"
	"time"

	log "github.com/sirupsen/logrus"
)

const (
	// THIM (Trusted Hardware Identity Management) endpoint on Azure IMDS.
	// The Metadata: true header is required — without it (or if set to
	// false) IMDS rejects the request with HTTP 400:
	//   {"error":"Bad request: . Required metadata header not specified"}
	// See: https://learn.microsoft.com/en-us/azure/security/fundamentals/trusted-hardware-identity-management
	thimURL            = "http://169.254.169.254/metadata/THIM/amd/certification"
	thimTimeoutS       = 300
	thimPollIntervalS  = 10
)

// WaitForTHIMProvisioningStage polls the IMDS THIM endpoint until
// it returns HTTP 200.
type WaitForTHIMProvisioningStage struct{}

func (s *WaitForTHIMProvisioningStage) Name() string {
	return "thimProvisioning"
}

func (s *WaitForTHIMProvisioningStage) Run(_ *Context) error {
	log.Infof(
		"Warming THIM certificate cache (timeout=%ds) ...",
		thimTimeoutS,
	)

	client := &http.Client{Timeout: 30 * time.Second}
	deadline := time.Now().Add(thimTimeoutS * time.Second)
	attempt := 0

	for {
		attempt++

		req, err := http.NewRequest("GET", thimURL, nil)
		if err != nil {
			return fmt.Errorf("creating THIM request: %w", err)
		}
		req.Header.Set("Metadata", "true")

		resp, err := client.Do(req)
		if err != nil {
			log.Warnf(
				"THIM request failed (attempt %d): %v", attempt, err,
			)
		} else {
			bodyBytes, _ := io.ReadAll(resp.Body)
			resp.Body.Close()

			if resp.StatusCode == 200 {
				log.Infof(
					"THIM cache warm (HTTP %d, %d bytes, attempt %d).",
					resp.StatusCode, len(bodyBytes), attempt,
				)
				return nil
			}
			log.Warnf(
				"THIM returned HTTP %d (attempt %d).",
				resp.StatusCode, attempt,
			)
		}

		if time.Now().After(deadline) {
			return fmt.Errorf(
				"THIM did not become ready within %ds after %d attempts."+
					" Attestation will fail",
				thimTimeoutS, attempt,
			)
		}

		log.Infof("Retrying THIM in %ds ...", thimPollIntervalS)
		time.Sleep(thimPollIntervalS * time.Second)
	}
}
