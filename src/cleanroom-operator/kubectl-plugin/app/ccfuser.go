package app

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"

	"github.com/spf13/cobra"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
)

func newCcfUserCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:     "ccf-user",
		Aliases: []string{"ccfuser"},
		Short:   "Manage CCF users",
	}

	cmd.AddCommand(newCcfUserGetAccessTokenCmd())
	return cmd
}

func newCcfUserGetAccessTokenCmd() *cobra.Command {
	var namespace string

	cmd := &cobra.Command{
		Use:   "get-access-token <ccf-user-name>",
		Short: "Get an access token from the user's governance client",
		Long: `Retrieves an access token from the governance client
associated with the specified CcfUser. The token can be used to
authenticate with inferencing services.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runCcfUserGetAccessToken(
				cmd.Context(), args[0], namespace,
			)
		},
	}

	cmd.Flags().StringVarP(
		&namespace, "namespace", "n", "",
		"Kubernetes namespace of the CcfUser",
	)
	return cmd
}

func runCcfUserGetAccessToken(
	ctx context.Context,
	name string,
	namespace string,
) error {
	client, err := getDynamicClient()
	if err != nil {
		return err
	}

	if namespace == "" {
		namespace = defaultNamespaceFromKubeconfig()
	}

	// Fetch the CcfUser CR.
	result, err := client.Resource(ccfUserGVR).
		Namespace(namespace).
		Get(ctx, name, metav1.GetOptions{})
	if err != nil {
		return fmt.Errorf(
			"getting CcfUser %s: %w", name, err,
		)
	}

	// Extract the governance client endpoint to
	// confirm the service exists.
	endpoint, found, _ := unstructured.NestedString(
		result.Object,
		"status", "governanceClientEndpoint",
	)
	if !found || endpoint == "" {
		return fmt.Errorf(
			"CcfUser %s does not have a "+
				"governance client endpoint yet",
			name,
		)
	}

	// Port-forward to the user's governance client
	// service.
	svcTarget := fmt.Sprintf(
		"svc/cgs-user-%s", name,
	)
	pf, port, err := startPortForwardToPort(
		namespace, svcTarget, 18490, 8080,
	)
	if err != nil {
		return fmt.Errorf(
			"port-forwarding to %s: %w",
			svcTarget, err,
		)
	}
	defer func() {
		if pf.Process != nil {
			_ = pf.Process.Kill()
		}
	}()

	// Call GET /identity/accessToken.
	url := fmt.Sprintf(
		"http://localhost:%d/identity/accessToken",
		port,
	)
	req, err := http.NewRequestWithContext(
		ctx, http.MethodGet, url, nil,
	)
	if err != nil {
		return fmt.Errorf(
			"creating request: %w", err,
		)
	}

	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return fmt.Errorf(
			"calling access token API: %w", err,
		)
	}
	defer resp.Body.Close()

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return fmt.Errorf(
			"reading response: %w", err,
		)
	}

	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf(
			"access token request failed (%d): %s",
			resp.StatusCode, string(body),
		)
	}

	// Extract the accessToken value from the response.
	var parsed map[string]any
	if err := json.Unmarshal(body, &parsed); err != nil {
		return fmt.Errorf(
			"parsing access token response: %w", err,
		)
	}

	token, ok := parsed["accessToken"].(string)
	if !ok || token == "" {
		return fmt.Errorf(
			"accessToken not found in response",
		)
	}

	fmt.Print(token)
	return nil
}
