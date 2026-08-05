package app

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"text/tabwriter"

	"github.com/spf13/cobra"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
	"k8s.io/apimachinery/pkg/runtime/schema"
	"k8s.io/client-go/dynamic"
)

func newInfoCmd() *cobra.Command {
	var namespace string
	var output string

	cmd := &cobra.Command{
		Use:   "info [name]",
		Short: "Show a summary of all resources in the namespace",
		Long: `Display an overview of all Clean Room resources
(Environments, CCF Networks, Clusters, Model Registrations,
and Model Deployments) and their current state.

Optionally pass an Environment name to filter to that
environment and its related resources.`,
		Args: cobra.MaximumNArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			var envFilter string
			if len(args) > 0 {
				envFilter = args[0]
			}
			return runInfo(
				cmd.Context(),
				namespace,
				output,
				envFilter,
			)
		},
	}

	f := cmd.Flags()
	f.StringVar(
		&namespace, "namespace",
		defaultNamespaceFromKubeconfig(),
		"Kubernetes namespace",
	)
	f.StringVarP(
		&output, "output", "o", "",
		"Output format (json)",
	)
	return cmd
}

// infoSummary holds the aggregated state for JSON
// output.
type infoSummary struct {
	Namespace    string        `json:"namespace"`
	Environments []infoEnv     `json:"environments"`
	CcfNetworks  []infoCcfNet  `json:"ccfNetworks"`
	Clusters     []infoCluster `json:"clusters"`
	ModelRegistrations []infoMR      `json:"modelRegistrations"`
	ModelDeploys  []infoMD     `json:"modelDeployments"`
}

type infoEnv struct {
	Name      string   `json:"name"`
	Phase     string   `json:"phase"`
	InfraType string   `json:"infraType"`
	Profiles  []string `json:"profiles,omitempty"`
}

type infoCcfNet struct {
	Name      string `json:"name"`
	Phase     string `json:"phase"`
	InfraType string `json:"infraType"`
	Endpoint  string `json:"endpoint,omitempty"`
}

type infoCluster struct {
	Name      string `json:"name"`
	Phase     string `json:"phase"`
	InfraType string `json:"infraType"`
}

type infoMR struct {
	Name        string `json:"name"`
	Model       string `json:"model"`
	Environment string `json:"environment"`
	Phase       string `json:"phase"`
}

type infoMD struct {
	Name            string `json:"name"`
	ModelRegistration string `json:"modelRegistration"`
	Phase           string `json:"phase"`
	Endpoint        string `json:"endpoint,omitempty"`
}

func runInfo(
	ctx context.Context,
	namespace, output, envFilter string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	summary, err := gatherInfo(
		ctx, client, namespace, envFilter,
	)
	if err != nil {
		return err
	}

	if output == "json" {
		enc := json.NewEncoder(os.Stdout)
		enc.SetIndent("", "  ")
		return enc.Encode(summary)
	}

	if envFilter != "" {
		printInfoTree(summary)
	} else {
		printInfoTables(summary)
	}
	return nil
}

func gatherInfo(
	ctx context.Context,
	client dynamic.Interface,
	namespace, envFilter string,
) (*infoSummary, error) {
	s := &infoSummary{Namespace: namespace}

	// Environments.
	envs, err := listResources(
		ctx, client, environmentGVR, namespace,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"listing Environments: %w", err,
		)
	}

	envNames := map[string]bool{}
	for _, item := range envs {
		name := item.GetName()
		if envFilter != "" && name != envFilter {
			continue
		}
		envNames[name] = true
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		infraType, _, _ := unstructured.NestedString(
			item.Object, "spec", "infraType",
		)
		profiles := collectProfiles(item)
		s.Environments = append(s.Environments, infoEnv{
			Name:      name,
			Phase:     phase,
			InfraType: infraType,
			Profiles:  profiles,
		})
	}

	if envFilter != "" && len(envNames) == 0 {
		return nil, fmt.Errorf(
			"Environment %q not found", envFilter,
		)
	}

	// CCF Networks.
	nets, err := listResources(
		ctx, client, ccfNetworkGVR, namespace,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"listing CcfNetworks: %w", err,
		)
	}

	for _, item := range nets {
		if envFilter != "" &&
			!isRelatedToEnv(item, envNames) {
			continue
		}
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		infraType, _, _ := unstructured.NestedString(
			item.Object, "spec", "infraType",
		)
		endpoint, _, _ := unstructured.NestedString(
			item.Object, "status", "endpoint",
		)
		s.CcfNetworks = append(
			s.CcfNetworks, infoCcfNet{
				Name:      item.GetName(),
				Phase:     phase,
				InfraType: infraType,
				Endpoint:  endpoint,
			},
		)
	}

	// Clusters.
	clusters, err := listResources(
		ctx, client, clusterGVR, namespace,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"listing Clusters: %w", err,
		)
	}

	for _, item := range clusters {
		if envFilter != "" &&
			!isRelatedToEnv(item, envNames) {
			continue
		}
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		infraType, _, _ := unstructured.NestedString(
			item.Object, "spec", "infraType",
		)
		s.Clusters = append(
			s.Clusters, infoCluster{
				Name:      item.GetName(),
				Phase:     phase,
				InfraType: infraType,
			},
		)
	}

	// Model Registrations.
	mds, err := listResources(
		ctx, client, modelRegistrationGVR, namespace,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"listing ModelRegistrations: %w", err,
		)
	}

	mdNames := map[string]bool{}
	for _, item := range mds {
		envRef, _, _ := unstructured.NestedString(
			item.Object, "spec", "environmentRef",
		)
		if envFilter != "" && !envNames[envRef] {
			continue
		}
		name := item.GetName()
		mdNames[name] = true
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		modelId, _, _ := unstructured.NestedString(
			item.Object, "spec", "model", "id",
		)
		s.ModelRegistrations = append(
			s.ModelRegistrations, infoMR{
				Name:        name,
				Model:       modelId,
				Environment: envRef,
				Phase:       phase,
			},
		)
	}

	// Model Deployments.
	mdis, err := listResources(
		ctx, client, modelDeploymentGVR, namespace,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"listing ModelDeployments: %w",
			err,
		)
	}

	for _, item := range mdis {
		mdRef, _, _ := unstructured.NestedString(
			item.Object,
			"spec", "modelRegistrationRef",
		)
		if envFilter != "" && !mdNames[mdRef] {
			continue
		}
		phase, _, _ := unstructured.NestedString(
			item.Object, "status", "phase",
		)
		endpoint, _, _ := unstructured.NestedString(
			item.Object,
			"status", "serviceEndpoint",
		)
		s.ModelDeploys = append(
			s.ModelDeploys, infoMD{
				Name:            item.GetName(),
				ModelRegistration: mdRef,
				Phase:           phase,
				Endpoint:        endpoint,
			},
		)
	}

	return s, nil
}

// printInfoTree renders a card-style header for the
// environment with a tree of child resources beneath it.
func printInfoTree(s *infoSummary) {
	if len(s.Environments) == 0 {
		fmt.Println("No matching environments found.")
		return
	}

	e := s.Environments[0]
	profiles := "-"
	if len(e.Profiles) > 0 {
		profiles = joinStrings(e.Profiles)
	}
	phase := e.Phase
	if phase == "" {
		phase = "<unknown>"
	}

	fmt.Printf("Environment: %s (%s)\n", e.Name, phase)
	fmt.Printf("  InfraType: %s\n", e.InfraType)
	fmt.Printf("  Profiles:  %s\n", profiles)
	fmt.Printf("  Namespace: %s\n", s.Namespace)
	fmt.Println()

	// Build the tree lines. We want:
	// ├── CcfNetwork/name (Phase)
	// ├── Cluster/name (Phase)
	// ├── ModelRegistration/name (Phase) model: <id>
	// │   └── ModelDeployment/name (Phase) endpoint: <url>
	// └── ModelRegistration/name2 ...
	type treeLine struct {
		text     string
		children []string
	}
	var lines []treeLine

	for _, n := range s.CcfNetworks {
		ep := ""
		if n.Endpoint != "" {
			ep = fmt.Sprintf(
				"  endpoint: %s", n.Endpoint,
			)
		}
		lines = append(lines, treeLine{
			text: fmt.Sprintf(
				"CcfNetwork/%s (%s)%s",
				n.Name, phaseOrUnknown(n.Phase), ep,
			),
		})
	}

	for _, c := range s.Clusters {
		lines = append(lines, treeLine{
			text: fmt.Sprintf(
				"Cluster/%s (%s)",
				c.Name, phaseOrUnknown(c.Phase),
			),
		})
	}

	// Group MDIs by their parent MD.
	mdByMR := map[string][]infoMD{}
	for _, i := range s.ModelDeploys {
		mdByMR[i.ModelRegistration] = append(
			mdByMR[i.ModelRegistration], i,
		)
	}

	for _, m := range s.ModelRegistrations {
		ml := treeLine{
			text: fmt.Sprintf(
				"ModelRegistration/%s (%s)"+
					"  model: %s",
				m.Name,
				phaseOrUnknown(m.Phase),
				m.Model,
			),
		}
		for _, i := range mdByMR[m.Name] {
			ep := ""
			if i.Endpoint != "" {
				ep = fmt.Sprintf(
					"  endpoint: %s", i.Endpoint,
				)
			}
			ml.children = append(ml.children,
				fmt.Sprintf(
					"ModelDeployment/%s"+
						" (%s)%s",
					i.Name,
					phaseOrUnknown(i.Phase),
					ep,
				),
			)
		}
		lines = append(lines, ml)
	}

	if len(lines) == 0 {
		fmt.Println("  (no child resources)")
		return
	}

	fmt.Println("Resources:")
	for i, line := range lines {
		isLast := i == len(lines)-1
		connector := "├── "
		childPrefix := "│   "
		if isLast {
			connector = "└── "
			childPrefix = "    "
		}
		fmt.Printf("  %s%s\n", connector, line.text)
		for j, child := range line.children {
			childConn := "├── "
			if j == len(line.children)-1 {
				childConn = "└── "
			}
			fmt.Printf(
				"  %s%s%s\n",
				childPrefix, childConn, child,
			)
		}
	}
	fmt.Println()
}

func phaseOrUnknown(phase string) string {
	if phase == "" {
		return "<unknown>"
	}
	return phase
}

// printInfoTables renders flat tables per resource type
// (used when no specific environment is selected).
func printInfoTables(s *infoSummary) {
	fmt.Printf("Namespace: %s\n", s.Namespace)
	fmt.Println()

	// Environments.
	if len(s.Environments) > 0 {
		fmt.Println("Environments:")
		w := tabwriter.NewWriter(
			os.Stdout, 0, 4, 2, ' ', 0,
		)
		fmt.Fprintf(w,
			"  NAME\tPHASE\tINFRA-TYPE\tPROFILES\n",
		)
		for _, e := range s.Environments {
			profiles := "-"
			if len(e.Profiles) > 0 {
				profiles = joinStrings(e.Profiles)
			}
			fmt.Fprintf(w, "  %s\t%s\t%s\t%s\n",
				e.Name, e.Phase,
				e.InfraType, profiles,
			)
		}
		w.Flush()
		fmt.Println()
	} else {
		fmt.Println("Environments: (none)")
		fmt.Println()
	}

	// CCF Networks.
	if len(s.CcfNetworks) > 0 {
		fmt.Println("CCF Networks:")
		w := tabwriter.NewWriter(
			os.Stdout, 0, 4, 2, ' ', 0,
		)
		fmt.Fprintf(w,
			"  NAME\tPHASE\tINFRA-TYPE\tENDPOINT\n",
		)
		for _, n := range s.CcfNetworks {
			endpoint := n.Endpoint
			if endpoint == "" {
				endpoint = "-"
			}
			fmt.Fprintf(w, "  %s\t%s\t%s\t%s\n",
				n.Name, n.Phase,
				n.InfraType, endpoint,
			)
		}
		w.Flush()
		fmt.Println()
	} else {
		fmt.Println("CCF Networks: (none)")
		fmt.Println()
	}

	// Clusters.
	if len(s.Clusters) > 0 {
		fmt.Println("Clusters:")
		w := tabwriter.NewWriter(
			os.Stdout, 0, 4, 2, ' ', 0,
		)
		fmt.Fprintf(w,
			"  NAME\tPHASE\tINFRA-TYPE\n",
		)
		for _, c := range s.Clusters {
			fmt.Fprintf(w, "  %s\t%s\t%s\n",
				c.Name, c.Phase, c.InfraType,
			)
		}
		w.Flush()
		fmt.Println()
	} else {
		fmt.Println("Clusters: (none)")
		fmt.Println()
	}

	// Model Registrations.
	if len(s.ModelRegistrations) > 0 {
		fmt.Println("Model Registrations:")
		w := tabwriter.NewWriter(
			os.Stdout, 0, 4, 2, ' ', 0,
		)
		fmt.Fprintf(w,
			"  NAME\tMODEL\tENVIRONMENT\tPHASE\n",
		)
		for _, m := range s.ModelRegistrations {
			fmt.Fprintf(w, "  %s\t%s\t%s\t%s\n",
				m.Name, m.Model,
				m.Environment, m.Phase,
			)
		}
		w.Flush()
		fmt.Println()
	} else {
		fmt.Println("Model Registrations: (none)")
		fmt.Println()
	}

	// Model Deployments.
	if len(s.ModelDeploys) > 0 {
		fmt.Println("Model Deployments:")
		w := tabwriter.NewWriter(
			os.Stdout, 0, 4, 2, ' ', 0,
		)
		fmt.Fprintf(w,
			"  NAME\tMODEL-REGISTRATION"+
				"\tPHASE\tENDPOINT\n",
		)
		for _, i := range s.ModelDeploys {
			endpoint := i.Endpoint
			if endpoint == "" {
				endpoint = "-"
			}
			fmt.Fprintf(w, "  %s\t%s\t%s\t%s\n",
				i.Name, i.ModelRegistration,
				i.Phase, endpoint,
			)
		}
		w.Flush()
		fmt.Println()
	} else {
		fmt.Println("Model Deployments: (none)")
		fmt.Println()
	}
}

func listResources(
	ctx context.Context,
	client dynamic.Interface,
	gvr schema.GroupVersionResource,
	namespace string,
) ([]unstructured.Unstructured, error) {
	list, err := client.Resource(gvr).
		Namespace(namespace).
		List(ctx, metav1.ListOptions{})
	if err != nil {
		return nil, err
	}
	return list.Items, nil
}

// isRelatedToEnv checks whether a child resource
// (CcfNetwork, Cluster) has an owner reference to
// one of the given environment names, or uses the
// conventional name-suffix pattern.
func isRelatedToEnv(
	item unstructured.Unstructured,
	envNames map[string]bool,
) bool {
	// Check ownerReferences first.
	owners := item.GetOwnerReferences()
	for _, ref := range owners {
		if ref.Kind == "Environment" &&
			envNames[ref.Name] {
			return true
		}
	}

	// Fall back to naming convention:
	// <env>-network, <env>-cluster, <env>-member.
	name := item.GetName()
	for envName := range envNames {
		if name == envName+"-network" ||
			name == envName+"-cluster" ||
			name == envName+"-member" {
			return true
		}
	}
	return false
}

func collectProfiles(
	item unstructured.Unstructured,
) []string {
	var profiles []string

	inferencing, _, _ := unstructured.NestedBool(
		item.Object,
		"spec", "profiles", "inferencing",
		"kserveProfile", "enabled",
	)
	if inferencing {
		profiles = append(profiles, "inferencing")
	}

	flexEnabled, _, _ := unstructured.NestedBool(
		item.Object,
		"spec", "profiles", "flexNode", "enabled",
	)
	if flexEnabled {
		profiles = append(profiles, "flex-node")
	}

	analytics, _, _ := unstructured.NestedBool(
		item.Object,
		"spec", "profiles", "analytics", "enabled",
	)
	if analytics {
		profiles = append(profiles, "analytics")
	}

	observability, _, _ := unstructured.NestedBool(
		item.Object,
		"spec", "profiles", "observability",
		"enabled",
	)
	if observability {
		profiles = append(profiles, "observability")
	}

	monitoring, _, _ := unstructured.NestedBool(
		item.Object,
		"spec", "profiles", "monitoring", "enabled",
	)
	if monitoring {
		profiles = append(profiles, "monitoring")
	}

	return profiles
}

func joinStrings(ss []string) string {
	result := ""
	for i, s := range ss {
		if i > 0 {
			result += ", "
		}
		result += s
	}
	return result
}
