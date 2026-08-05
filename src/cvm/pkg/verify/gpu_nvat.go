package verify

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"os/exec"
	"sort"
	"strings"
	"time"
)

const (
	nvatBinaryPath = "nvattest"

	// nvatAttestTimeout bounds the NVAT local appraisal subprocess.
	nvatAttestTimeout = 120 * time.Second
)

type gpuNVATAttestOutput struct {
	ClaimsRaw     json.RawMessage `json:"claims"`
	ResultCode    int             `json:"result_code"`
	ResultMessage string          `json:"result_message"`
}

type gpuNVATClaims struct {
	MeasurementResult              *string `json:"measres"`
	SecureBoot                     *bool   `json:"secboot"`
	DebugState                     *string `json:"dbgstat"`
	DriverRIMFetched               *bool   `json:"x-nvidia-gpu-driver-rim-fetched"`
	DriverRIMSignatureVerified     *bool   `json:"x-nvidia-gpu-driver-rim-signature-verified"`
	DriverRIMVersionMatch          *bool   `json:"x-nvidia-gpu-driver-rim-version-match"`
	DriverRIMMeasurementsAvailable *bool   `json:"x-nvidia-gpu-driver-rim-measurements-available"`
	VBIOSRIMFetched                *bool   `json:"x-nvidia-gpu-vbios-rim-fetched"`
	VBIOSRIMSignatureVerified      *bool   `json:"x-nvidia-gpu-vbios-rim-signature-verified"`
	VBIOSRIMVersionMatch           *bool   `json:"x-nvidia-gpu-vbios-rim-version-match"`
	VBIOSRIMMeasurementsAvailable  *bool   `json:"x-nvidia-gpu-vbios-rim-measurements-available"`
	VBIOSIndexNoConflict           *bool   `json:"x-nvidia-gpu-vbios-index-no-conflict"`
}

type requiredBoolClaim struct {
	Name  string
	Value *bool
}

func verifyGPURIMAppraisal(
	gpuInput *GpuInput,
	cpuReportData []byte,
	cpuReportDataErr error,
) []NamedCheck {
	if gpuInput == nil || len(gpuInput.Evidences) == 0 {
		return nil
	}

	if cpuReportDataErr != nil {
		return []NamedCheck{{
			ID: "gpuRIMAppraisal",
			Result: CheckResult{
				Passed: false,
				Error: fmt.Sprintf(
					"failed to derive NVAT nonce from CPU report_data: %v", cpuReportDataErr),
			},
		}}
	}

	nvatResult, err := runNVATLocalAttest(gpuInput, cpuReportData)
	if err != nil {
		return []NamedCheck{{
			ID: "gpuRIMAppraisal",
			Result: CheckResult{
				Passed: false,
				Error:  fmt.Sprintf("nvattest local appraisal failed: %v", err),
			},
		}}
	}

	if nvatResult.ResultCode != 0 {
		return []NamedCheck{{
			ID: "gpuRIMAppraisal",
			Result: CheckResult{
				Passed: false,
				Error: fmt.Sprintf(
					"nvattest local appraisal failed: code=%d, msg=%s",
					nvatResult.ResultCode,
					nvatResult.ResultMessage),
			},
		}}
	}

	claims, err := parseNVATClaims(nvatResult.ClaimsRaw)
	if err != nil {
		return []NamedCheck{{
			ID: "gpuRIMAppraisal",
			Result: CheckResult{
				Passed: false,
				Error:  err.Error(),
			},
		}}
	}

	if len(claims) != len(gpuInput.Evidences) {
		return []NamedCheck{{
			ID: "gpuRIMAppraisal",
			Result: CheckResult{
				Passed: false,
				Error: fmt.Sprintf(
					"nvattest returned %d claim(s) for %d GPU evidence(s)",
					len(claims),
					len(gpuInput.Evidences)),
			},
		}}
	}

	results := []NamedCheck{{
		ID: "gpuRIMAppraisal",
		Result: CheckResult{
			Passed: true,
			Detail: fmt.Sprintf(
				"nvattest locally appraised %d GPU(s) against NVIDIA RIMs",
				len(claims)),
		},
	}}

	for i := range claims {
		prefix := fmt.Sprintf("gpu%d", i)
		results = append(results, verifyGPURIMClaims(prefix, &claims[i])...)
	}

	return results
}

func verifyGPURIMClaims(prefix string, claims *gpuNVATClaims) []NamedCheck {
	var results []NamedCheck
	addCheck := func(id string, check CheckResult) {
		results = append(results, NamedCheck{ID: fmt.Sprintf("%s.%s", prefix, id), Result: check})
	}

	if claims.MeasurementResult == nil {
		addCheck("rimAppraisal", CheckResult{
			Passed: false,
			Error:  "NVAT claim 'measres' is missing",
		})
	} else if strings.EqualFold(strings.TrimSpace(*claims.MeasurementResult), "success") {
		addCheck("rimAppraisal", CheckResult{
			Passed: true,
			Detail: "NVAT measured-state appraisal succeeded",
		})
	} else {
		addCheck("rimAppraisal", CheckResult{
			Passed: false,
			Error:  fmt.Sprintf("NVAT measured-state appraisal result is %q", *claims.MeasurementResult),
		})
	}

	if claims.SecureBoot == nil {
		addCheck("secureBoot", CheckResult{
			Passed: false,
			Error:  "NVAT claim 'secboot' is missing",
		})
	} else if *claims.SecureBoot {
		addCheck("secureBoot", CheckResult{
			Passed: true,
			Detail: "NVAT secure boot claim is true",
		})
	} else {
		addCheck("secureBoot", CheckResult{
			Passed: false,
			Error:  "NVAT secure boot claim is false",
		})
	}

	if claims.DebugState == nil {
		addCheck("debugState", CheckResult{
			Passed: false,
			Error:  "NVAT claim 'dbgstat' is missing",
		})
	} else if strings.EqualFold(strings.TrimSpace(*claims.DebugState), "disabled") {
		addCheck("debugState", CheckResult{
			Passed: true,
			Detail: "NVAT debug-state claim is disabled",
		})
	} else {
		addCheck("debugState", CheckResult{
			Passed: false,
			Error:  fmt.Sprintf("NVAT debug-state claim is %q", *claims.DebugState),
		})
	}

	if err := requireTrueClaims([]requiredBoolClaim{
		{Name: "x-nvidia-gpu-driver-rim-fetched", Value: claims.DriverRIMFetched},
		{Name: "x-nvidia-gpu-driver-rim-signature-verified", Value: claims.DriverRIMSignatureVerified},
		{Name: "x-nvidia-gpu-driver-rim-version-match", Value: claims.DriverRIMVersionMatch},
		{Name: "x-nvidia-gpu-driver-rim-measurements-available", Value: claims.DriverRIMMeasurementsAvailable},
	}); err != nil {
		addCheck("driverRIM", CheckResult{Passed: false, Error: err.Error()})
	} else {
		addCheck("driverRIM", CheckResult{
			Passed: true,
			Detail: "NVAT driver RIM fetched, signed, version-matched, and measurements available",
		})
	}

	if err := requireTrueClaims([]requiredBoolClaim{
		{Name: "x-nvidia-gpu-vbios-rim-fetched", Value: claims.VBIOSRIMFetched},
		{Name: "x-nvidia-gpu-vbios-rim-signature-verified", Value: claims.VBIOSRIMSignatureVerified},
		{Name: "x-nvidia-gpu-vbios-rim-version-match", Value: claims.VBIOSRIMVersionMatch},
		{Name: "x-nvidia-gpu-vbios-rim-measurements-available", Value: claims.VBIOSRIMMeasurementsAvailable},
		{Name: "x-nvidia-gpu-vbios-index-no-conflict", Value: claims.VBIOSIndexNoConflict},
	}); err != nil {
		addCheck("vbiosRIM", CheckResult{Passed: false, Error: err.Error()})
	} else {
		addCheck("vbiosRIM", CheckResult{
			Passed: true,
			Detail: "NVAT VBIOS RIM fetched, signed, version-matched, and measurements available",
		})
	}

	return results
}

func requireTrueClaims(claims []requiredBoolClaim) error {
	var missing []string
	var failed []string

	for _, claim := range claims {
		if claim.Value == nil {
			missing = append(missing, claim.Name)
			continue
		}

		if !*claim.Value {
			failed = append(failed, claim.Name)
		}
	}

	if len(missing) > 0 {
		sort.Strings(missing)
		return fmt.Errorf("missing NVAT claim(s): %s", strings.Join(missing, ", "))
	}

	if len(failed) > 0 {
		sort.Strings(failed)
		return fmt.Errorf("NVAT claim(s) false: %s", strings.Join(failed, ", "))
	}

	return nil
}

func runNVATLocalAttest(gpuInput *GpuInput, cpuReportData []byte) (*gpuNVATAttestOutput, error) {
	if len(cpuReportData) != spdmNonceSize {
		return nil, fmt.Errorf(
			"NVAT nonce must be exactly %d bytes, got %d", spdmNonceSize, len(cpuReportData))
	}

	evidenceFilePath, cleanup, err := writeNVATEvidenceFile(gpuInput)
	if err != nil {
		return nil, err
	}
	defer cleanup()

	ctx, cancel := context.WithTimeout(context.Background(), nvatAttestTimeout)
	defer cancel()

	cmd := exec.CommandContext(ctx,
		nvatBinaryPath,
		"--format", "json",
		"--log-level", "error",
		"attest",
		"--device", "gpu",
		"--verifier", "local",
		"--gpu-evidence-source", "file",
		"--gpu-evidence-file", evidenceFilePath,
		"--nonce", hex.EncodeToString(cpuReportData),
	)

	var stdout bytes.Buffer
	var stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr

	runErr := cmd.Run()
	output := bytes.TrimSpace(stdout.Bytes())
	stderrText := strings.TrimSpace(stderr.String())
	if len(output) == 0 {
		if runErr != nil {
			return nil, fmt.Errorf("%s attest failed: %w (stderr: %s)",
				nvatBinaryPath, runErr, stderrText)
		}

		if stderrText != "" {
			return nil, fmt.Errorf("%s returned no JSON output (stderr: %s)",
				nvatBinaryPath, stderrText)
		}

		return nil, fmt.Errorf("%s returned no JSON output", nvatBinaryPath)
	}

	var result gpuNVATAttestOutput
	if err := json.Unmarshal(output, &result); err != nil {
		if stderrText != "" {
			return nil, fmt.Errorf(
				"failed to parse %s JSON output: %w (stderr: %s, raw: %s)",
				nvatBinaryPath,
				err,
				stderrText,
				string(output))
		}

		return nil, fmt.Errorf(
			"failed to parse %s JSON output: %w (raw: %s)",
			nvatBinaryPath,
			err,
			string(output))
	}

	return &result, nil
}

func parseNVATClaims(raw json.RawMessage) ([]gpuNVATClaims, error) {
	if len(raw) == 0 || bytes.Equal(raw, []byte("null")) {
		return nil, fmt.Errorf("nvattest returned no appraisal claims")
	}

	var claims []gpuNVATClaims
	if err := json.Unmarshal(raw, &claims); err != nil {
		return nil, fmt.Errorf("failed to parse nvattest appraisal claims: %w", err)
	}

	return claims, nil
}

func writeNVATEvidenceFile(gpuInput *GpuInput) (string, func(), error) {
	file, err := os.CreateTemp("", "nvat-gpu-evidence-*.json")
	if err != nil {
		return "", nil, fmt.Errorf("create temp GPU evidence file: %w", err)
	}

	cleanup := func() {
		_ = os.Remove(file.Name())
	}

	if err := json.NewEncoder(file).Encode(gpuInput.Evidences); err != nil {
		_ = file.Close()
		cleanup()
		return "", nil, fmt.Errorf("write temp GPU evidence file: %w", err)
	}

	if err := file.Close(); err != nil {
		cleanup()
		return "", nil, fmt.Errorf("close temp GPU evidence file: %w", err)
	}

	return file.Name(), cleanup, nil
}
