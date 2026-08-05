package app

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"github.com/spf13/cobra"
	"helm.sh/helm/v3/pkg/cli"
	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/apis/meta/v1/unstructured"
	"k8s.io/client-go/kubernetes"
	"sigs.k8s.io/yaml"
)

const (
	keyBundleLabelKey   = "cleanroom.azure.com/key-bundle"
	keyBundleLabelValue = "true"
	envLabelKey         = "cleanroom.azure.com/environment"
	idpKeysSecretSuffix = "-idp-keys"

	localIdpSvcPort = 8399

	flexNodeSshSecretName = "cleanroom-flex-node-ssh-key"
)

type devSaveOpts struct {
	namespace string
	output    string
}

type devRestoreOpts struct {
	input string
}

func newDevSaveCmd() *cobra.Command {
	o := &devSaveOpts{}

	cmd := &cobra.Command{
		Use:   "save <env-name>",
		Short: "Save crypto keys and user IDs for an environment",
		Long: `Export CCF member keys, local-IDP signing keys, and CcfUser
identity mappings to a YAML file. This file can be used with
'dev restore' to recreate a management cluster that is compatible
with the existing CCF network and workload clusters.

Must be run while the management cluster is still running.`,
		Args: cobra.ExactArgs(1),
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runDevSave(
				cmd.Context(), args[0], o,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(
		&o.namespace, "namespace", "n", "default",
		"Namespace of the environment",
	)
	f.StringVarP(
		&o.output, "output", "o", "",
		"Output file path",
	)
	_ = cmd.MarkFlagRequired("output")
	return cmd
}

func newDevRestoreCmd() *cobra.Command {
	o := &devRestoreOpts{}

	cmd := &cobra.Command{
		Use:   "restore",
		Short: "Restore crypto keys and user IDs from a save file",
		Long: `Import a previously saved key bundle into the current
management cluster. Creates the individual CcfMember Secrets,
local-IDP keys Secret, and user-IDs ConfigMap so that subsequent
'environment create' commands will reuse the original identities.

Must be run after 'dev up' but before 'environment create'.`,
		RunE: func(
			cmd *cobra.Command, args []string,
		) error {
			return runDevRestore(
				cmd.Context(), o,
			)
		},
	}

	f := cmd.Flags()
	f.StringVarP(
		&o.input, "input", "i", "",
		"Path to the save file created by 'dev save'",
	)
	_ = cmd.MarkFlagRequired("input")
	return cmd
}

// runDevSave exports all keys and user IDs for the given
// environment into a single YAML file.
func runDevSave(
	ctx context.Context,
	envName string,
	o *devSaveOpts,
) error {
	clientset, err := getClientset()
	if err != nil {
		return err
	}

	ns := o.namespace
	bundleData := map[string][]byte{}

	// 1. Discover member cert Secrets by label.
	memberSelector := fmt.Sprintf(
		"cleanroom.azure.com/member-certs=true,"+
			"cleanroom.azure.com/environment=%s",
		envName,
	)
	fmt.Printf(
		"Listing member cert Secrets "+
			"(selector: %s)...\n",
		memberSelector,
	)
	memberSecrets, err := clientset.CoreV1().Secrets(
		ns,
	).List(ctx, metav1.ListOptions{
		LabelSelector: memberSelector,
	})
	if err != nil {
		return fmt.Errorf(
			"listing member cert Secrets: %w", err,
		)
	}
	if len(memberSecrets.Items) == 0 {
		return fmt.Errorf(
			"no member cert Secrets found for "+
				"environment %s in namespace %s",
			envName, ns,
		)
	}

	type memberSecretEntry struct {
		Name   string            `json:"name"`
		Labels map[string]string `json:"labels"`
	}
	var manifest []memberSecretEntry

	for _, sec := range memberSecrets.Items {
		fmt.Printf(
			"  Reading %s/%s...\n",
			ns, sec.Name,
		)
		manifest = append(manifest,
			memberSecretEntry{
				Name:   sec.Name,
				Labels: sec.Labels,
			},
		)
		for k, v := range sec.Data {
			bundleData[sec.Name+"/"+k] = v
		}
	}

	manifestJSON, err := json.Marshal(manifest)
	if err != nil {
		return fmt.Errorf(
			"marshaling member manifest: %w", err,
		)
	}
	bundleData["_member-secrets.json"] = manifestJSON

	// 2. Export local-IDP keys via /exportkeys endpoint.
	fmt.Println("Exporting local-IDP signing keys...")
	idpKeys, err := exportIdpKeysViaPortForward(
		ctx, clientset, ns,
	)
	if err != nil {
		return fmt.Errorf(
			"exporting local-IDP keys: %w", err,
		)
	}
	if idpKeys == nil {
		fmt.Println(
			"  (no signing key generated yet, skipping)",
		)
	} else {
		bundleData["idp-signing-privk.pem"] =
			[]byte(idpKeys.PrivkPEM)
		bundleData["idp-signing-pubk.pem"] =
			[]byte(idpKeys.PubkPEM)
		bundleData["idp-signing-cert.pem"] =
			[]byte(idpKeys.CertPEM)
		bundleData["idp-signing-kid"] =
			[]byte(idpKeys.KID)
	}

	// 3. Read user-IDs ConfigMaps by label.
	fmt.Println("Listing user-IDs ConfigMaps...")
	userIdsCMs, err := clientset.CoreV1().ConfigMaps(
		ns,
	).List(ctx, metav1.ListOptions{
		LabelSelector: "cleanroom.azure.com/user-ids=true",
	})
	if err != nil {
		return fmt.Errorf(
			"listing user-IDs ConfigMaps: %w", err,
		)
	}
	if len(userIdsCMs.Items) == 0 {
		fmt.Println(
			"  (no user-ids ConfigMap found, skipping)",
		)
	}
	for _, cm := range userIdsCMs.Items {
		fmt.Printf(
			"  Reading ConfigMap %s/%s...\n",
			ns, cm.Name,
		)
		cmEntry := map[string]interface{}{
			"name":   cm.Name,
			"labels": cm.Labels,
			"data":   cm.Data,
		}
		cmJSON, jsonErr := json.Marshal(cmEntry)
		if jsonErr != nil {
			return fmt.Errorf(
				"marshaling ConfigMap %s: %w",
				cm.Name, jsonErr,
			)
		}
		bundleData["_configmap-"+cm.Name+".json"] =
			cmJSON
	}

	// 4. Save flex-node SSH key Secret if it exists.
	fmt.Println("Reading flex-node SSH key Secret...")
	sshSecret, err := clientset.CoreV1().Secrets(
		ns,
	).Get(ctx, flexNodeSshSecretName, metav1.GetOptions{})
	if err != nil {
		if apierrors.IsNotFound(err) {
			fmt.Println(
				"  (no SSH key Secret found, skipping)",
			)
		} else {
			return fmt.Errorf(
				"reading SSH key Secret: %w", err,
			)
		}
	} else {
		for k, v := range sshSecret.Data {
			bundleData["ssh/"+k] = v
		}
		fmt.Printf(
			"  ✓ SSH key Secret %s/%s read\n",
			ns, flexNodeSshSecretName,
		)
	}

	// Build the key-bundle Secret YAML.
	bundleSecret := &corev1.Secret{
		TypeMeta: metav1.TypeMeta{
			APIVersion: "v1",
			Kind:       "Secret",
		},
		ObjectMeta: metav1.ObjectMeta{
			Name:      envName + "-key-bundle",
			Namespace: ns,
			Labels: map[string]string{
				keyBundleLabelKey: keyBundleLabelValue,
				envLabelKey:       envName,
			},
			Annotations: map[string]string{
				"cleanroom.azure.com/saved-at": time.Now().
					UTC().Format(time.RFC3339),
			},
		},
		Type: corev1.SecretTypeOpaque,
		Data: bundleData,
	}

	yamlBytes, err := yaml.Marshal(bundleSecret)
	if err != nil {
		return fmt.Errorf(
			"marshaling key-bundle to YAML: %w", err,
		)
	}

	// Write to file.
	outputPath := o.output
	if err := os.WriteFile(
		outputPath, yamlBytes, 0600,
	); err != nil {
		return fmt.Errorf(
			"writing save file: %w", err,
		)
	}

	abs, _ := filepath.Abs(outputPath)
	fmt.Printf(
		"\nSaved key bundle to %s\n"+
			"Use 'kubectl cleanroom dev restore "+
			"--input %s' after recreating the cluster.\n",
		abs, abs,
	)
	return nil
}

// runDevRestore reads a save file and creates the
// individual Secrets and ConfigMap in the cluster.
func runDevRestore(
	ctx context.Context,
	o *devRestoreOpts,
) error {
	data, err := os.ReadFile(o.input)
	if err != nil {
		return fmt.Errorf(
			"reading save file: %w", err,
		)
	}

	var bundle corev1.Secret
	if err := yaml.Unmarshal(data, &bundle); err != nil {
		return fmt.Errorf(
			"parsing save file: %w", err,
		)
	}

	envName := bundle.Labels[envLabelKey]
	if envName == "" {
		return fmt.Errorf(
			"save file missing %s label", envLabelKey,
		)
	}

	ns := bundle.Namespace
	if ns == "" {
		ns = "default"
	}

	clientset, err := getClientset()
	if err != nil {
		return err
	}

	fmt.Printf(
		"Restoring keys for environment '%s' in "+
			"namespace '%s'...\n",
		envName, ns,
	)

	// 1. Restore member cert Secrets from manifest.
	if manifestJSON, ok :=
		bundle.Data["_member-secrets.json"]; ok {
		type memberSecretEntry struct {
			Name   string            `json:"name"`
			Labels map[string]string `json:"labels"`
		}
		var manifest []memberSecretEntry
		if err := json.Unmarshal(
			manifestJSON, &manifest,
		); err != nil {
			return fmt.Errorf(
				"parsing member manifest: %w", err,
			)
		}
		for _, entry := range manifest {
			secretData := extractPrefixedKeys(
				bundle.Data, entry.Name+"/",
			)
			if len(secretData) == 0 {
				continue
			}
			if err := ensureSecretWithLabels(
				ctx, clientset, ns,
				entry.Name, secretData,
				entry.Labels,
			); err != nil {
				return fmt.Errorf(
					"restoring %s: %w",
					entry.Name, err,
				)
			}
			fmt.Printf(
				"  ✓ Secret %s restored\n",
				entry.Name,
			)
		}
	}

	// 2. Restore local-IDP keys Secret.
	// Create in the environment namespace and also in
	// cleanroom-system (where local-IDP runs) so the
	// Helm chart can mount it.
	idpSecretName := envName + idpKeysSecretSuffix
	idpData := map[string][]byte{}
	keyMap := map[string]string{
		"idp-signing-privk.pem": "signing-privk.pem",
		"idp-signing-pubk.pem":  "signing-pubk.pem",
		"idp-signing-cert.pem":  "signing-cert.pem",
		"idp-signing-kid":       "signing-kid",
	}
	for bundleKey, secretKey := range keyMap {
		if v, ok := bundle.Data[bundleKey]; ok {
			idpData[secretKey] = v
		}
	}
	if len(idpData) > 0 {
		// Create in the environment namespace.
		if err := ensureSecret(
			ctx, clientset, ns,
			idpSecretName, idpData,
		); err != nil {
			return fmt.Errorf(
				"restoring %s: %w",
				idpSecretName, err,
			)
		}
		fmt.Printf(
			"  ✓ Secret %s/%s restored\n",
			ns, idpSecretName,
		)

		// Also create in operator namespace if different.
		if ns != operatorNamespace {
			if err := ensureSecret(
				ctx, clientset, operatorNamespace,
				idpSecretName, idpData,
			); err != nil {
				return fmt.Errorf(
					"restoring %s in %s: %w",
					idpSecretName, operatorNamespace, err,
				)
			}
			fmt.Printf(
				"  ✓ Secret %s/%s restored\n",
				operatorNamespace, idpSecretName,
			)
		}

		// Re-deploy local-IDP so it picks up the
		// restored keys via the volume mount.
		fmt.Println("  Upgrading local-idp to use " +
			"restored keys...")
		settings := cli.New()
		if kubeconfigPath != "" {
			settings.KubeConfig = kubeconfigPath
		}
		if err := deployLocalIdp(
			ctx, settings, operatorNamespace,
			"", "", "",
		); err != nil {
			return fmt.Errorf(
				"upgrading local-idp: %w", err,
			)
		}
		fmt.Println("  ✓ local-idp upgraded")
	}

	// 3. Restore user-IDs ConfigMaps from bundle.
	for bundleKey, raw := range bundle.Data {
		if !isConfigMapBundleKey(bundleKey) {
			continue
		}
		var cmEntry struct {
			Name   string            `json:"name"`
			Labels map[string]string `json:"labels"`
			Data   map[string]string `json:"data"`
		}
		if err := json.Unmarshal(
			raw, &cmEntry,
		); err != nil {
			return fmt.Errorf(
				"parsing %s: %w", bundleKey, err,
			)
		}
		if err := ensureConfigMap(
			ctx, clientset, ns,
			cmEntry.Name, cmEntry.Data,
			cmEntry.Labels,
		); err != nil {
			return fmt.Errorf(
				"restoring ConfigMap %s: %w",
				cmEntry.Name, err,
			)
		}
		fmt.Printf(
			"  ✓ ConfigMap %s restored\n",
			cmEntry.Name,
		)
	}

	// 4. Restore flex-node SSH key Secret from bundle.
	sshData := extractPrefixedKeys(
		bundle.Data, "ssh/",
	)
	if len(sshData) > 0 {
		if err := ensureSecret(
			ctx, clientset, ns,
			flexNodeSshSecretName, sshData,
		); err != nil {
			return fmt.Errorf(
				"restoring %s: %w",
				flexNodeSshSecretName, err,
			)
		}
		fmt.Printf(
			"  ✓ Secret %s/%s restored\n",
			ns, flexNodeSshSecretName,
		)
	}

	fmt.Printf(
		"\nRestore complete. You can now run "+
			"'kubectl cleanroom environment create %s'.\n",
		envName,
	)
	return nil
}

// extractPrefixedKeys returns entries from data whose key
// starts with prefix, with the prefix stripped.
func extractPrefixedKeys(
	data map[string][]byte,
	prefix string,
) map[string][]byte {
	result := map[string][]byte{}
	for k, v := range data {
		if len(k) > len(prefix) &&
			k[:len(prefix)] == prefix {
			result[k[len(prefix):]] = v
		}
	}
	return result
}

// ensureSecret creates or updates a Secret.
func ensureSecret(
	ctx context.Context,
	clientset *kubernetes.Clientset,
	namespace string,
	name string,
	data map[string][]byte,
) error {
	return ensureSecretWithLabels(
		ctx, clientset, namespace,
		name, data, nil,
	)
}

// ensureSecretWithLabels creates or updates a Secret
// with the given labels.
func ensureSecretWithLabels(
	ctx context.Context,
	clientset *kubernetes.Clientset,
	namespace string,
	name string,
	data map[string][]byte,
	labels map[string]string,
) error {
	secret := &corev1.Secret{
		ObjectMeta: metav1.ObjectMeta{
			Name:      name,
			Namespace: namespace,
			Labels:    labels,
		},
		Type: corev1.SecretTypeOpaque,
		Data: data,
	}

	_, err := clientset.CoreV1().Secrets(namespace).Create(
		ctx, secret, metav1.CreateOptions{},
	)
	if apierrors.IsAlreadyExists(err) {
		existing, getErr := clientset.CoreV1().Secrets(
			namespace,
		).Get(ctx, name, metav1.GetOptions{})
		if getErr != nil {
			return getErr
		}
		existing.Data = data
		if labels != nil {
			if existing.Labels == nil {
				existing.Labels = map[string]string{}
			}
			for k, v := range labels {
				existing.Labels[k] = v
			}
		}
		_, err = clientset.CoreV1().Secrets(
			namespace,
		).Update(ctx, existing, metav1.UpdateOptions{})
	}
	return err
}

// ensureConfigMap creates or updates a ConfigMap.
func ensureConfigMap(
	ctx context.Context,
	clientset *kubernetes.Clientset,
	namespace string,
	name string,
	data map[string]string,
	labels map[string]string,
) error {
	cm := &corev1.ConfigMap{
		ObjectMeta: metav1.ObjectMeta{
			Name:      name,
			Namespace: namespace,
			Labels:    labels,
		},
		Data: data,
	}

	_, err := clientset.CoreV1().ConfigMaps(
		namespace,
	).Create(ctx, cm, metav1.CreateOptions{})
	if apierrors.IsAlreadyExists(err) {
		existing, getErr := clientset.CoreV1().ConfigMaps(
			namespace,
		).Get(ctx, name, metav1.GetOptions{})
		if getErr != nil {
			return getErr
		}
		// Merge — don't overwrite entries not in the
		// save file.
		if existing.Data == nil {
			existing.Data = map[string]string{}
		}
		for k, v := range data {
			existing.Data[k] = v
		}
		if labels != nil {
			if existing.Labels == nil {
				existing.Labels = map[string]string{}
			}
			for k, v := range labels {
				existing.Labels[k] = v
			}
		}
		_, err = clientset.CoreV1().ConfigMaps(
			namespace,
		).Update(ctx, existing, metav1.UpdateOptions{})
	}
	return err
}

// isConfigMapBundleKey checks if a bundle data key
// represents a saved ConfigMap entry.
func isConfigMapBundleKey(key string) bool {
	const prefix = "_configmap-"
	const suffix = ".json"
	return len(key) > len(prefix)+len(suffix) &&
		key[:len(prefix)] == prefix &&
		key[len(key)-len(suffix):] == suffix
}

// idpExportKeysResponse mirrors the local-IDP
// /exportkeys JSON response.
type idpExportKeysResponse struct {
	KID      string `json:"kid"`
	PrivkPEM string `json:"privk_pem"`
	PubkPEM  string `json:"pubk_pem"`
	CertPEM  string `json:"cert_pem"`
	X5C      string `json:"x5c"`
}

// exportIdpKeysViaPortForward calls the local-IDP
// /exportkeys endpoint using kubectl port-forward.
func exportIdpKeysViaPortForward(
	ctx context.Context,
	clientset *kubernetes.Clientset,
	namespace string,
) (*idpExportKeysResponse, error) {
	// Find the local-idp pod.
	pods, err := clientset.CoreV1().Pods(
		operatorNamespace,
	).List(ctx, metav1.ListOptions{
		LabelSelector: "app=local-idp",
	})
	if err != nil {
		return nil, fmt.Errorf(
			"listing local-idp pods: %w", err,
		)
	}
	if len(pods.Items) == 0 {
		return nil, fmt.Errorf(
			"no local-idp pods found in %s namespace",
			operatorNamespace,
		)
	}

	podName := pods.Items[0].Name
	cmd, port, err := startPortForwardToPort(
		operatorNamespace, podName,
		18399, localIdpSvcPort,
	)
	if err != nil {
		return nil, fmt.Errorf(
			"port-forwarding to local-idp: %w", err,
		)
	}
	defer func() {
		if cmd.Process != nil {
			_ = cmd.Process.Kill()
		}
	}()

	url := fmt.Sprintf(
		"http://localhost:%d/exportkeys", port,
	)
	httpClient := &http.Client{Timeout: 15 * time.Second}
	resp, err := httpClient.Get(url)
	if err != nil {
		return nil, fmt.Errorf(
			"GET /exportkeys: %w", err,
		)
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusNoContent {
		// No signing key generated yet.
		return nil, nil
	}
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf(
			"GET /exportkeys returned %d",
			resp.StatusCode,
		)
	}

	var keys idpExportKeysResponse
	if err := json.NewDecoder(resp.Body).Decode(
		&keys,
	); err != nil {
		return nil, fmt.Errorf(
			"parsing /exportkeys response: %w", err,
		)
	}
	return &keys, nil
}

// devSaveAllEnvironments saves keys for all environments
// in the cluster. Used by 'dev down --save'.
func devSaveAllEnvironments(
	ctx context.Context,
) error {
	clientset, err := getClientset()
	if err != nil {
		return err
	}

	// List all Environment CRs.
	envs, err := clientset.CoreV1().RESTClient().Get().
		AbsPath("/apis/cleanroom.azure.com/v1alpha1").
		Resource("environments").
		DoRaw(ctx)
	if err != nil {
		return fmt.Errorf(
			"listing environments: %w", err,
		)
	}

	var envList unstructured.UnstructuredList
	if err := json.Unmarshal(
		envs, &envList,
	); err != nil {
		return fmt.Errorf(
			"parsing environment list: %w", err,
		)
	}

	if len(envList.Items) == 0 {
		fmt.Println("No environments found, skipping save.")
		return nil
	}

	for _, env := range envList.Items {
		name := env.GetName()
		ns := env.GetNamespace()
		if ns == "" {
			ns = "default"
		}
		outputPath := filepath.Join(
			"generated", name+"-save.yaml",
		)
		fmt.Printf(
			"\nSaving environment '%s'...\n", name,
		)
		if err := runDevSave(ctx, name, &devSaveOpts{
			namespace: ns,
			output:    outputPath,
		}); err != nil {
			fmt.Printf(
				"  Warning: failed to save '%s': %v\n",
				name, err,
			)
		}
	}
	return nil
}
