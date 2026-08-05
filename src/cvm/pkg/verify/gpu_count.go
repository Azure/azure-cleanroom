package verify

import (
	"encoding/base64"
	"encoding/json"
	"fmt"

	"github.com/azure/azure-cleanroom/src/cvm/pkg/attestation"
)

// MetadataBindingResult holds the outcome of user data document verification.
type MetadataBindingResult struct {
	Passed   bool
	Checks   []NamedCheck
	Document *attestation.UserDataDocument
}

// VerifyMetadataDocumentBinding verifies the user data document hash against
// the signed report_data and checks that the gpuCount matches the supplied
// GPU evidence count.
func VerifyMetadataDocumentBinding(
	docB64 string,
	gpuInput *GpuInput,
	runtimeClaims json.RawMessage,
) MetadataBindingResult {
	result := MetadataBindingResult{Passed: true}

	var claims attestation.RuntimeClaims
	if err := json.Unmarshal(runtimeClaims, &claims); err != nil {
		result.Passed = false
		result.Checks = append(result.Checks, NamedCheck{
			ID: "metadataBinding",
			Result: CheckResult{
				Passed: false,
				Error:  fmt.Sprintf("parse runtime claims: %v", err),
			},
		})
		return result
	}

	if claims.UserData == "" {
		result.Passed = false
		result.Checks = append(result.Checks, NamedCheck{
			ID: "metadataBinding",
			Result: CheckResult{
				Passed: false,
				Error:  "runtime claims missing user-data",
			},
		})
		return result
	}

	if docB64 == "" {
		result.Passed = false
		result.Checks = append(result.Checks, NamedCheck{
			ID: "metadataBinding",
			Result: CheckResult{
				Passed: false,
				Error:  "userDataDocument is missing",
			},
		})
		return result
	}

	docJSON, decodeErr := base64.StdEncoding.DecodeString(docB64)
	if decodeErr != nil {
		result.Passed = false
		result.Checks = append(result.Checks, NamedCheck{
			ID: "metadataBinding",
			Result: CheckResult{
				Passed: false,
				Error:  fmt.Sprintf("invalid base64 userDataDocument: %v", decodeErr),
			},
		})
		return result
	}

	doc, err := attestation.VerifyUserDataDocumentBinding(
		claims.UserData, docJSON)
	if err != nil {
		result.Passed = false
		result.Checks = append(result.Checks, NamedCheck{
			ID: "metadataBinding",
			Result: CheckResult{
				Passed: false,
				Error:  err.Error(),
			},
		})
		return result
	}

	result.Document = doc
	result.Checks = append(result.Checks, NamedCheck{
		ID: "metadataBinding",
		Result: CheckResult{
			Passed: true,
			Detail: fmt.Sprintf(
				"user data document hash matches user-data[0:32] (version=%d, gpuCount=%d)",
				doc.Version, doc.GPUCount),
		},
	})

	// Verify GPU count matches supplied evidence count.
	suppliedGPUCount := 0
	if gpuInput != nil {
		suppliedGPUCount = len(gpuInput.Evidences)
	}

	if doc.GPUCount == suppliedGPUCount {
		result.Checks = append(result.Checks, NamedCheck{
			ID: "gpuCountBinding",
			Result: CheckResult{
				Passed: true,
				Detail: fmt.Sprintf(
					"signed GPU count %d matches supplied GPU evidence count %d",
					doc.GPUCount, suppliedGPUCount),
			},
		})
	} else {
		result.Passed = false
		result.Checks = append(result.Checks, NamedCheck{
			ID: "gpuCountBinding",
			Result: CheckResult{
				Passed: false,
				Error: fmt.Sprintf(
					"signed GPU count %d does not match supplied GPU evidence count %d",
					doc.GPUCount, suppliedGPUCount),
			},
		})
	}

	return result
}
