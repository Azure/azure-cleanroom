package verify

import (
	"strings"
)

// GPUClaims is the normalized verifier-owned GPU policy surface returned to CGS.
type GPUClaims struct {
	GPUCount         int   `json:"gpuCount"`
	GPURIMAppraisal  *bool `json:"gpuRIMAppraisal,omitempty"`
	GPUSecureBoot    *bool `json:"gpuSecureBoot,omitempty"`
	GPUDebugDisabled *bool `json:"gpuDebugDisabled,omitempty"`
	GPUDriverRIM     *bool `json:"gpuDriverRIM,omitempty"`
	GPUVbiosRIM      *bool `json:"gpuVbiosRIM,omitempty"`
}

// PopulateGPUClaimsFromChecks maps per-GPU verifier checks into normalized
// policy claims. Only per-device appraisal checks are surfaced here.
func PopulateGPUClaimsFromChecks(claims *GPUClaims, gpuChecks []NamedCheck) {
	if claims == nil || claims.GPUCount == 0 {
		return
	}

	type aggregateCheck struct {
		seen   bool
		passed bool
	}

	rimAppraisal := aggregateCheck{passed: true}
	secureBoot := aggregateCheck{passed: true}
	debugDisabled := aggregateCheck{passed: true}
	driverRIM := aggregateCheck{passed: true}
	vbiosRIM := aggregateCheck{passed: true}

	for _, check := range gpuChecks {
		switch {
		case strings.HasSuffix(check.ID, ".rimAppraisal"):
			rimAppraisal.seen = true
			rimAppraisal.passed = rimAppraisal.passed && check.Result.Passed
		case strings.HasSuffix(check.ID, ".secureBoot"):
			secureBoot.seen = true
			secureBoot.passed = secureBoot.passed && check.Result.Passed
		case strings.HasSuffix(check.ID, ".debugState"):
			debugDisabled.seen = true
			debugDisabled.passed = debugDisabled.passed && check.Result.Passed
		case strings.HasSuffix(check.ID, ".driverRIM"):
			driverRIM.seen = true
			driverRIM.passed = driverRIM.passed && check.Result.Passed
		case strings.HasSuffix(check.ID, ".vbiosRIM"):
			vbiosRIM.seen = true
			vbiosRIM.passed = vbiosRIM.passed && check.Result.Passed
		}
	}

	if rimAppraisal.seen {
		claims.GPURIMAppraisal = boolPtr(rimAppraisal.passed)
	}
	if secureBoot.seen {
		claims.GPUSecureBoot = boolPtr(secureBoot.passed)
	}
	if debugDisabled.seen {
		claims.GPUDebugDisabled = boolPtr(debugDisabled.passed)
	}
	if driverRIM.seen {
		claims.GPUDriverRIM = boolPtr(driverRIM.passed)
	}
	if vbiosRIM.seen {
		claims.GPUVbiosRIM = boolPtr(vbiosRIM.passed)
	}
}

func boolPtr(value bool) *bool {
	return &value
}
