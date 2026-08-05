package app

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"github.com/spf13/cobra"
)

type generateEnvOpts struct {
	repo   string
	tag    string
	outDir string
}

func newDevGenerateEnvCmd() *cobra.Command {
	o := &generateEnvOpts{}

	cmd := &cobra.Command{
		Use:   "generate-env",
		Short: "Generate dev-up.env file for a published container set",
		Long: `Generates the dev-up.env file that points the
cleanroom operator and provider clients at a specific
container registry and tag. This is the equivalent of
build/onebox/generate-dev-up-env.ps1 but does not
require PowerShell or a local build.

Example:
  kubectl cleanroom dev generate-env \
    --repo myregistry.azurecr.io \
    --tag 0.20.0 \
    --outdir ./generated`,
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runGenerateEnv(o)
		},
	}

	f := cmd.Flags()
	f.StringVar(&o.repo, "repo", "",
		"Container registry URL "+
			"(e.g. myregistry.azurecr.io)")
	_ = cmd.MarkFlagRequired("repo")
	f.StringVar(&o.tag, "tag", "",
		"Image tag / version "+
			"(e.g. 0.20.0 or a commit hash)")
	_ = cmd.MarkFlagRequired("tag")
	f.StringVar(&o.outDir, "outdir", ".",
		"Directory to write dev-up.env into")
	return cmd
}

func runGenerateEnv(o *generateEnvOpts) error {
	repo := o.repo
	tag := o.tag

	// Compute OCI endpoint (for Helm chart refs that
	// need a pod-reachable address).
	ociEndpoint := repo
	if strings.HasPrefix(repo, "localhost:5000") {
		if os.Getenv("CODESPACES") == "true" {
			ociEndpoint = "ccr-registry:5000"
		} else if os.Getenv(
			"GITHUB_ACTIONS",
		) == "true" {
			ociEndpoint = "172.17.0.1:5000"
		} else {
			ociEndpoint = "host.docker.internal:5000"
		}
	}

	podReachableRepo := repo
	if repo == "localhost:5000" {
		podReachableRepo = "ccr-registry:5000"
	}

	semVer := semanticVersionFromTag(tag)
	useHTTP := "false"
	if repo == "localhost:5000" {
		useHTTP = "true"
	}

	type section struct {
		header string
		vars   [][2]string
	}

	sections := []section{
		{
			header: "cluster-provider",
			vars: [][2]string{
				{"AZCLI_CLEANROOM_OPERATOR_IMAGE",
					repo + "/cleanroom-operator:" + tag},
				{"AZCLI_CLEANROOM_KARPENTER_PROVIDER_IMAGE",
					repo + "/karpenter-provider-accr:" + tag},
				{"AZCLI_CLEANROOM_KARPENTER_PROVIDER_CHART_URL",
					ociEndpoint + "/helm/karpenter-provider-accr:" + semVer},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_CLIENT_IMAGE",
					repo + "/cleanroom-cluster/cleanroom-cluster-provider-client:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_PROXY_IMAGE",
					repo + "/ccr-proxy:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_OTEL_COLLECTOR_IMAGE",
					repo + "/otel-collector:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_LOCAL_SKR_IMAGE",
					repo + "/local-skr:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_SKR_IMAGE",
					repo + "/skr:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_VIRTUAL_IMAGE",
					repo + "/ccr-governance-virtual:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_GOVERNANCE_IMAGE",
					repo + "/ccr-governance:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_IMAGE",
					repo + "/workloads/cleanroom-spark-analytics-agent:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_CHART_URL",
					ociEndpoint + "/workloads/helm/cleanroom-spark-analytics-agent:" + semVer},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_ANALYTICS_AGENT_SECURITY_POLICY_DOCUMENT_URL",
					repo + "/policies/workloads/cleanroom-spark-analytics-agent-security-policy:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_IMAGE",
					repo + "/workloads/cleanroom-spark-frontend:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_CHART_URL",
					ociEndpoint + "/workloads/helm/cleanroom-spark-frontend:" + semVer},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_SPARK_FRONTEND_SECURITY_POLICY_DOCUMENT_URL",
					repo + "/policies/workloads/cleanroom-spark-frontend-security-policy:" + tag},
				{"AZCLI_CLEANROOM_SPARK_FRONTEND_VERSIONS_DOCUMENT_URL",
					repo + "/versions/workloads/cleanroom-spark-frontend:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_IMAGE",
					repo + "/workloads/kserve-inferencing-agent:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_CHART_URL",
					ociEndpoint + "/workloads/helm/kserve-inferencing-agent:" + semVer},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_AGENT_SECURITY_POLICY_DOCUMENT_URL",
					repo + "/policies/workloads/cleanroom-kserve-inferencing-agent-security-policy:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_OHTTP_GATEWAY_IMAGE",
					repo + "/workloads/ohttp-gateway:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_IMAGE",
					repo + "/workloads/kserve-inferencing-frontend:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_CHART_URL",
					ociEndpoint + "/workloads/helm/kserve-inferencing-frontend:" + semVer},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_KSERVE_INFERENCING_FRONTEND_SECURITY_POLICY_DOCUMENT_URL",
					repo + "/policies/workloads/cleanroom-kserve-inferencing-frontend-security-policy:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_API_SERVER_PROXY_PACKAGE_URL",
					ociEndpoint + "/k8s-node/api-server-proxy:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_KUBELET_PROXY_PACKAGE_URL",
					ociEndpoint + "/k8s-node/kubelet-proxy:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_URL",
					repo},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_TAG",
					tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_CONTAINER_REGISTRY_USE_HTTP",
					useHTTP},
				{"AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_URL",
					repo + "/workloads/cleanroom-spark-analytics-app:" + tag},
				{"AZCLI_CLEANROOM_CLUSTER_PROVIDER_HOST_SHARED_DIR",
					"/tmp/cleanroom-shared"},
				{"AZCLI_CLEANROOM_SIDECARS_POLICY_DOCUMENT_REGISTRY_URL",
					podReachableRepo},
				{"AZCLI_CLEANROOM_ANALYTICS_APP_IMAGE_POLICY_DOCUMENT_URL",
					podReachableRepo + "/policies/workloads/cleanroom-spark-analytics-app-security-policy:" + tag},
				{"AZCLI_CLEANROOM_SIDECARS_VERSIONS_DOCUMENT_URL",
					podReachableRepo + "/sidecar-digests:" + tag},
				{"AZCLI_CLEANROOM_CVM_MEASUREMENTS_VIRTUAL_DOCUMENT_URL",
					podReachableRepo + "/cvm-measurements-virtual:" + tag},
				{"AZCLI_CLEANROOM_CVM_MEASUREMENTS_DOCUMENT_URL",
					podReachableRepo + "/cvm-measurements:" + tag},
				{"AZCLI_CLEANROOM_INFERENCING_DIGESTS_DOCUMENT_URL",
					podReachableRepo + "/inferencing-digests:" + tag},
			},
		},
		{
			header: "ccf-provider",
			vars: [][2]string{
				{"AZCLI_CCF_PROVIDER_CLIENT_IMAGE",
					repo + "/ccf/ccf-provider-client:" + tag},
				{"AZCLI_CCF_PROVIDER_PROXY_IMAGE",
					repo + "/ccr-proxy:" + tag},
				{"AZCLI_CCF_PROVIDER_SKR_IMAGE",
					repo + "/skr:" + tag},
				{"AZCLI_CCF_PROVIDER_LOCAL_SKR_IMAGE",
					repo + "/local-skr:" + tag},
				{"AZCLI_CCF_PROVIDER_RUN_JS_APP_VIRTUAL_IMAGE",
					repo + "/ccf/app/run-js/virtual:" + tag},
				{"AZCLI_CCF_PROVIDER_RUN_JS_APP_SNP_IMAGE",
					repo + "/ccf/app/run-js/snp:" + tag},
				{"AZCLI_CCF_PROVIDER_RECOVERY_AGENT_IMAGE",
					repo + "/ccf/ccf-recovery-agent:" + tag},
				{"AZCLI_CCF_PROVIDER_CVM_ATTESTATION_VERIFIER_IMAGE",
					repo + "/cvm/cvm-attestation-verifier:" + tag},
				{"AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_IMAGE",
					repo + "/ccf/ccf-recovery-service:" + tag},
				{"AZCLI_CCF_PROVIDER_CONSORTIUM_MANAGER_IMAGE",
					repo + "/ccf/ccf-consortium-manager:" + tag},
				{"AZCLI_CCF_PROVIDER_CONTAINER_REGISTRY_URL",
					repo},
				{"AZCLI_CCF_PROVIDER_NETWORK_SECURITY_POLICY_DOCUMENT_URL",
					repo + "/policies/ccf/ccf-network-security-policy:" + tag},
				{"AZCLI_CCF_PROVIDER_RECOVERY_SERVICE_SECURITY_POLICY_DOCUMENT_URL",
					repo + "/policies/ccf/ccf-recovery-service-security-policy:" + tag},
				{"AZCLI_CCF_PROVIDER_CONSORTIUM_MANAGER_SECURITY_POLICY_DOCUMENT_URL",
					repo + "/policies/ccf/ccf-consortium-manager-security-policy:" + tag},
			},
		},
		{
			header: "governance-client",
			vars: [][2]string{
				{"AZCLI_CGS_CLIENT_IMAGE",
					repo + "/cgs-client:" + tag},
				{"AZCLI_CGS_UI_IMAGE",
					repo + "/cgs-ui:" + tag},
				{"AZCLI_CGS_CONSTITUTION_IMAGE",
					podReachableRepo + "/cgs-constitution:" + tag},
				{"AZCLI_CGS_JS_APP_IMAGE",
					podReachableRepo + "/cgs-js-app:" + tag},
			},
		},
	}

	// Build file content.
	var sb strings.Builder
	for i, sec := range sections {
		sb.WriteString("# [" + sec.header + "]\n")
		for _, kv := range sec.vars {
			sb.WriteString(kv[0] + "=" + kv[1] + "\n")
		}
		if i < len(sections)-1 {
			sb.WriteString("\n")
		}
	}

	// Ensure output directory exists.
	if err := os.MkdirAll(o.outDir, 0o755); err != nil {
		return fmt.Errorf(
			"creating output directory: %w", err,
		)
	}

	outPath := filepath.Join(o.outDir, "dev-up.env")
	if err := os.WriteFile(
		outPath, []byte(sb.String()), 0o644,
	); err != nil {
		return fmt.Errorf(
			"writing env file: %w", err,
		)
	}

	fmt.Printf("Env file written to:\n  %s\n", outPath)
	return nil
}

// semanticVersionFromTag converts a tag like "0.20.0"
// or a commit hash into a Helm-compatible semver string.
var semverRe = regexp.MustCompile(
	`^\d+\.\d+\.\d+`,
)

func semanticVersionFromTag(tag string) string {
	if semverRe.MatchString(tag) {
		parts := strings.SplitN(tag, ".", 4)
		ver := parts[0] + "." + parts[1] + "." +
			parts[2]
		if len(parts) == 4 {
			ver += "-" + parts[3]
		}
		return ver
	}

	// Non-version tag: truncate to 8 chars and use
	// as a prerelease suffix.
	t := tag
	if len(t) > 8 {
		t = t[:8]
	}
	return "1.0.42-v" + t
}
