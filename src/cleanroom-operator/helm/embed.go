// Package helm provides the embedded Helm chart for the
// cleanroom-operator.
package helm

import (
	"embed"
	"os"
)

//go:embed all:chart
var chartFS embed.FS

//go:embed all:aspire-dashboard
var aspireDashboardFS embed.FS

//go:embed all:local-idp
var localIdpFS embed.FS

//go:embed all:karpenter-provider-accr
var karpenterProviderFS embed.FS

// CopyChartToDir extracts the embedded Helm chart to the given
// destination directory.
func CopyChartToDir(dstDir string) error {
	return copyDir(chartFS, "chart", dstDir)
}

// CopyAspireDashboardChartToDir extracts the embedded
// aspire-dashboard Helm chart to the given destination directory.
func CopyAspireDashboardChartToDir(dstDir string) error {
	return copyDir(aspireDashboardFS, "aspire-dashboard", dstDir)
}

// CopyLocalIdpChartToDir extracts the embedded local-idp
// Helm chart to the given destination directory.
func CopyLocalIdpChartToDir(dstDir string) error {
	return copyDir(localIdpFS, "local-idp", dstDir)
}

// CopyKarpenterProviderChartToDir extracts the embedded
// karpenter-provider-accr Helm chart to the given destination
// directory.
func CopyKarpenterProviderChartToDir(dstDir string) error {
	return copyDir(
		karpenterProviderFS,
		"karpenter-provider-accr",
		dstDir,
	)
}

func copyDir(fsys embed.FS, srcDir, dstDir string) error {
	entries, err := fsys.ReadDir(srcDir)
	if err != nil {
		return err
	}

	for _, entry := range entries {
		srcPath := srcDir + "/" + entry.Name()
		dstPath := dstDir + "/" + entry.Name()

		if entry.IsDir() {
			if err := os.MkdirAll(dstPath, 0o755); err != nil {
				return err
			}
			if err := copyDir(fsys, srcPath, dstPath); err != nil {
				return err
			}
		} else {
			data, err := fsys.ReadFile(srcPath)
			if err != nil {
				return err
			}
			if err := os.WriteFile(
				dstPath, data, 0o644,
			); err != nil {
				return err
			}
		}
	}
	return nil
}
